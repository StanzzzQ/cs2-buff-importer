using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuffBridge;

/// <summary>
/// 只走一条路：BUFF 的「在游戏中检视」接口。
///
///   /api/market/cs2_inspect?assetid=...  →  steam://...csgo_econ_action_preview &lt;hex&gt;
///   hex 是 protobuf CEconItemPreviewDataBlock，用 <see cref="InspectCodec"/> 解。
///
/// 这个接口需要登录态，所以 Cookie 是硬依赖；失效时抛 <see cref="BuffError.CookieRejected"/>，
/// 由插件提示玩家用 !buffcookie 提供新的。
/// </summary>
internal sealed class BuffClient : IDisposable
{
    private const string InspectApi = "/api/market/cs2_inspect?assetid={0}&_={1}";

    private static readonly Regex AssetIdPattern =
        new(@"[?&]assetid=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareDigitsPattern =
        new(@"^\d{4,}$", RegexOptions.Compiled);

    /// <summary>分享短码，例如 u86YQwb6；至少一个字母，避免和 assetid 混淆。</summary>
    private static readonly Regex ShareCodePattern =
        new(@"^(?=.*[A-Za-z])[A-Za-z0-9]{4,20}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _userAgent;

    private string _cookie = string.Empty;

    public BuffClient(BuffOptions options)
    {
        _userAgent = string.IsNullOrWhiteSpace(options.UserAgent)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
            : options.UserAgent;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://buff.163.com/"),
            Timeout = TimeSpan.FromSeconds(20),
        };

        SetCookie(options.Cookie);
    }

    public bool HasCookie => !string.IsNullOrWhiteSpace(_cookie);

    public void SetCookie(string? value)
    {
        _cookie = NormalizeCookie(value ?? string.Empty);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>链接 → assetid → 检视数据。</summary>
    public async Task<BuffItem> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        var link = input.Trim().Trim('"', '\'');

        if (link.Length == 0)
        {
            throw new BuffException(BuffError.InvalidLink, "链接是空的");
        }

        var assetId = await ResolveAssetIdAsync(link, cancellationToken).ConfigureAwait(false);

        if (!HasCookie)
        {
            throw new BuffException(BuffError.NoCookie, "还没有 BUFF Cookie")
            {
                AssetId = assetId,
            };
        }

        var decoded = await InspectAsync(assetId, cancellationToken).ConfigureAwait(false);
        return ToItem(decoded, assetId);
    }

    /// <summary>拿一个 assetid 试一下 Cookie 是否可用（补 Cookie 之后用）。</summary>
    public async Task<bool> ValidateAsync(string assetId, CancellationToken cancellationToken)
    {
        try
        {
            await InspectAsync(assetId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BuffException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- assetid

    private async Task<string> ResolveAssetIdAsync(string rawLink, CancellationToken cancellationToken)
    {
        var input = rawLink.Trim().Trim('"', '\'');

        // 裸数字按 assetid 处理：goods id 拿不到具体这一件，没有意义
        if (BareDigitsPattern.IsMatch(input))
        {
            return input;
        }

        var link = NormalizeLink(input);

        var direct = AssetIdPattern.Match(link);
        if (direct.Success)
        {
            return direct.Groups[1].Value;
        }

        if (!link.Contains("buff.163.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new BuffException(BuffError.InvalidLink, "这不是 BUFF 链接");
        }

        var uri = new Uri(link);

        using var response = await _http
            .SendAsync(CreateRequest(uri.PathAndQuery), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new BuffException(BuffError.Forbidden, "BUFF 返回 403（IP 被风控）");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new BuffException(BuffError.RateLimited, "请求太频繁，被 BUFF 限流");
        }

        var location = response.Headers.Location?.ToString();

        if (string.IsNullOrEmpty(location))
        {
            throw new BuffException(
                BuffError.InvalidLink,
                "这个链接里没有 assetid，请用 BUFF 里「分享」生成的单品链接");
        }

        var match = AssetIdPattern.Match(location);
        if (!match.Success)
        {
            throw new BuffException(BuffError.InvalidLink, "分享链接没有指向具体某一件饰品");
        }

        return match.Groups[1].Value;
    }

    /// <summary>
    /// 控制台（Source 引擎）把 `//` 之后的内容当注释截掉：`sw_bbtest https://...` 只会传进来 `https:`。
    /// 所以额外支持分享短码和省略 scheme 的写法：
    ///   u86YQwb6                   → https://buff.163.com/market/s/u86YQwb6
    ///   buff.163.com/market/s/xxx  → https://buff.163.com/market/s/xxx
    /// </summary>
    private static string NormalizeLink(string link)
    {
        if (link.Length == 0)
        {
            throw new BuffException(BuffError.InvalidLink, "链接是空的");
        }

        if (link.Equals("https:", StringComparison.OrdinalIgnoreCase)
            || link.Equals("http:", StringComparison.OrdinalIgnoreCase)
            || link.Equals("https", StringComparison.OrdinalIgnoreCase)
            || link.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new BuffException(
                BuffError.InvalidLink,
                "控制台把 // 后面的内容当注释截断了；请改用分享码，例如：sw_bbtest u86YQwb6");
        }

        if (ShareCodePattern.IsMatch(link))
        {
            return "https://buff.163.com/market/s/" + link;
        }

        if (!link.Contains("://", StringComparison.Ordinal))
        {
            return "https://" + link;
        }

        return link;
    }

    // ---------------------------------------------------------------- 检视接口

    private async Task<DecodedInspect> InspectAsync(string assetId, CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = string.Format(CultureInfo.InvariantCulture, InspectApi, assetId, stamp);

        using var response = await _http
            .SendAsync(CreateRequest(path), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var status = (int)response.StatusCode;

        if (status is 301 or 302 or 303 or 307 or 308)
        {
            var location = response.Headers.Location?.ToString() ?? string.Empty;

            if (location.Contains("/account/login", StringComparison.OrdinalIgnoreCase))
            {
                throw new BuffException(BuffError.CookieRejected, "BUFF 要求登录，Cookie 已失效")
                {
                    AssetId = assetId,
                };
            }

            throw new BuffException(BuffError.Unknown, $"BUFF 跳转到 {location}")
            {
                AssetId = assetId,
            };
        }

        if (status == 403)
        {
            throw new BuffException(BuffError.Forbidden, "BUFF 返回 403（IP 被风控，换 Cookie 没用）")
            {
                AssetId = assetId,
            };
        }

        if (status == 429)
        {
            throw new BuffException(BuffError.RateLimited, "请求太频繁，被 BUFF 限流")
            {
                AssetId = assetId,
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new BuffException(BuffError.Unknown, $"BUFF 返回 HTTP {status}")
            {
                AssetId = assetId,
            };
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new BuffException(BuffError.Unknown, "BUFF 返回的不是 JSON")
            {
                AssetId = assetId,
            };
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                var code = root.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "?";
                var msg = root.TryGetProperty("msg", out var msgElement) ? msgElement.ToString() : null;

                throw new BuffException(
                    BuffError.NotFound,
                    string.IsNullOrWhiteSpace(msg) ? $"BUFF 返回 {code}" : $"BUFF 返回 {code}：{msg}")
                {
                    AssetId = assetId,
                };
            }

            var inspectUrl = data.GetString();
            var decoded = string.IsNullOrWhiteSpace(inspectUrl) ? null : InspectCodec.Decode(inspectUrl);

            if (decoded is null)
            {
                throw new BuffException(BuffError.Unknown, "检视数据解码失败")
                {
                    AssetId = assetId,
                };
            }

            return decoded;
        }
    }

    // ---------------------------------------------------------------- 工具

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Referer", "https://buff.163.com/market/csgo");

        if (HasCookie)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        return request;
    }

    private static BuffItem ToItem(DecodedInspect decoded, string assetId)
    {
        return new BuffItem(
            decoded.PaintIndex,
            decoded.PaintSeed,
            decoded.PaintWear,
            decoded.IsStatTrak,
            assetId,
            decoded.DefinitionIndex > 0 ? (ushort)decoded.DefinitionIndex : (ushort)0)
        {
            Stickers = decoded.Stickers,
            Keychain = decoded.Keychain,
            NameTag = decoded.NameTag,
        };
    }

    /// <summary>玩家可能整段粘贴 "Cookie: session=..."，这里统一去掉前缀和换行。</summary>
    internal static string NormalizeCookie(string raw)
    {
        var value = raw.Trim().Trim('"', '\'');

        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Cookie:".Length..].Trim();
        }

        value = value.Replace("\r", string.Empty).Replace("\n", string.Empty);

        return value.Contains('=') ? value : string.Empty;
    }
}
