using System.Text.Json;

namespace BuffBridge;

/// <summary>插件目录下的 config.json，缺省值可直接跑。</summary>
internal sealed class BuffOptions
{
    /// <summary>请求头 UA。BUFF 对非常规 UA 很敏感，尽量用真浏览器串。</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// 登录后的 Cookie（至少 session=...）。
    /// 也可以留空，让玩家用 !buffcookie 现场提供（会写进 cookie.txt）。
    /// </summary>
    public string Cookie { get; set; } = string.Empty;

    /// <summary>HTTP(S) 代理，形如 http://127.0.0.1:7890。服务器 IP 被 BUFF 风控时用。</summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>每个玩家的冷却秒数。</summary>
    public int CooldownSeconds { get; set; } = 3;

    /// <summary>
    /// 应用后立刻刷新玩家手上的武器（否则要重新购买/捡枪才会变外观）。
    /// 通过反射调用 WeaponSkins 内部的 RefreshPlayer；失败会自动跳过。
    /// </summary>
    public bool RefreshAfterApply { get; set; } = true;

    /// <summary>
    /// 把换肤结果写进 WeaponSkins 的数据库（重启后仍然保留）。
    /// 设为 false 则只在本次运行内有效。
    /// </summary>
    public bool PersistSkins { get; set; } = true;

    public static BuffOptions Load(string pluginDirectory)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, "config.json");
            if (!File.Exists(path))
            {
                return new BuffOptions();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BuffOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new BuffOptions();
        }
        catch
        {
            // 配置坏了也要能跑，直接用默认值
            return new BuffOptions();
        }
    }
}
