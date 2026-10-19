using System.Globalization;
using System.Reflection;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using WeaponSkins;
using WeaponSkins.Shared;

namespace BuffBridge;

[PluginMetadata(
    Id = "BuffBridge",
    Version = "0.4.2",
    Name = "BuffBridge",
    Author = "StanzzzQ",
    Description = "!buff <BUFF 分享链接>：把武器/刀/手套换成链接里的皮肤（含印花、挂件）")]
public sealed class BuffBridgePlugin : BasePlugin
{
    private static readonly string PluginVersion =
        typeof(BuffBridgePlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>DLL 的写入时间，用来判断服务器上是不是真的换了新文件。</summary>
    private static readonly string BuildStamp = GetBuildStamp();

    private static string GetBuildStamp()
    {
        try
        {
            var path = typeof(BuffBridgePlugin).Assembly.Location;
            return string.IsNullOrEmpty(path) || !File.Exists(path)
                ? "未知"
                : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "未知";
        }
    }

    private readonly Dictionary<ulong, DateTime> _lastUse = new();

    private IWeaponSkinAPI? _skins;
    private BuffClient? _client;
    private TimeSpan _cooldown = TimeSpan.FromSeconds(3);
    private bool _refreshAfterApply = true;
    private bool _persistSkins = true;

    private string _cookieFile = string.Empty;
    private string _proxyInfo = string.Empty;

    /// <summary>Cookie 失效时记下这一件，补完 Cookie 立刻拿它验证。</summary>
    private string? _pendingAssetId;

    public BuffBridgePlugin(ISwiftlyCore core) : base(core)
    {
    }

    // WeaponSkins 在 ConfigureSharedInterface 里注册 "WeaponSkins.API"，这里取出来。
    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        base.UseSharedInterface(interfaceManager);
        _skins = interfaceManager.GetSharedInterface<IWeaponSkinAPI>("WeaponSkins.API");
    }

    public override void Load(bool hotReload)
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(BuffBridgePlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;

        var options = BuffOptions.Load(pluginDirectory);

        _cooldown = TimeSpan.FromSeconds(Math.Clamp(options.CooldownSeconds, 0, 3600));
        _cookieFile = Path.Combine(pluginDirectory, "cookie.txt");
        _proxyInfo = options.Proxy ?? string.Empty;
        _refreshAfterApply = options.RefreshAfterApply;
        _persistSkins = options.PersistSkins;

        _client = new BuffClient(options);

        // cookie.txt 优先于 config.json：运行期用 !buffcookie 改的就是这个文件
        var cookie = options.Cookie;
        try
        {
            if (File.Exists(_cookieFile))
            {
                var fromFile = File.ReadAllText(_cookieFile).Trim();
                if (fromFile.Length > 0)
                {
                    cookie = fromFile;
                }
            }
        }
        catch
        {
            // 读不到就用 config.json 里的
        }

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            _client.SetCookie(cookie);
        }
    }

    public override void Unload()
    {
        _client?.Dispose();
        _client = null;
        _lastUse.Clear();
    }

    // ---------------------------------------------------------------- !buff

    [Command("buff")]
    public void OnBuffCommand(ICommandContext context)
    {
        var player = context.Sender;
        if (player is null || !player.IsValid || !(player.PlayerPawn?.IsValid ?? false))
        {
            return;
        }

        var client = _client;
        var skins = _skins;
        if (client is null || skins is null)
        {
            context.Reply("» 插件未就绪：WeaponSkins 没加载或版本不匹配");
            return;
        }

        if (context.Args.Length == 0 || string.IsNullOrWhiteSpace(context.Args[0]))
        {
            context.Reply("» 用法：!buff <分享码 或 链接>（例：!buff u86YQwb6）");
            return;
        }

        var steamId = player.Controller.SteamID;
        var now = DateTime.UtcNow;
        if (_lastUse.TryGetValue(steamId, out var last) && now - last < _cooldown)
        {
            context.Reply("» 别急，稍等一下再试");
            return;
        }

        _lastUse[steamId] = now;

        var link = context.Args[0];

        _ = Task.Run(async () =>
        {
            try
            {
                var item = await client.ResolveAsync(link, CancellationToken.None).ConfigureAwait(false);
                Core.Scheduler.NextTick(() => ApplyAndReport(context, skins, item));
            }
            catch (BuffException ex) when (ex.Kind is BuffError.NoCookie or BuffError.CookieRejected)
            {
                _pendingAssetId = ex.AssetId;
                Core.Scheduler.NextTick(() => PromptForCookie(context, ex));
            }
            catch (BuffException ex)
            {
                Core.Scheduler.NextTick(() => context.Reply($"» 换肤失败：{ex.Message}"));
            }
            catch (Exception ex)
            {
                Core.Scheduler.NextTick(() => context.Reply($"» 换肤失败：{ex.Message}"));
            }
        });
    }

    /// <summary>Cookie 不可用时的引导：把 assetid 记下来，让玩家现场贴一条新的。</summary>
    private void PromptForCookie(ICommandContext context, BuffException ex)
    {
        context.Reply("» BUFF 登录态不可用，读不到这件饰品的数据");
        context.Reply($"» 原因：{ex.Message}");
        context.Reply("» 请发送：!buffcookie session=xxxx");

        if (!string.IsNullOrEmpty(ex.AssetId))
        {
            context.Reply($"» 补完之后重新发 !buff，我会接着验证 assetid={ex.AssetId}");
        }
    }

    // ---------------------------------------------------------------- !buffcookie

    [Command("buffcookie")]
    public void OnCookieCommand(ICommandContext context)
    {
        var client = _client;
        if (client is null)
        {
            context.Reply("» 插件未就绪");
            return;
        }

        var raw = string.Join(" ", context.Args).Trim();

        if (raw.Length == 0 || raw.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            context.Reply("» 用法：!buffcookie session=xxxx（整串粘贴，可带 Cookie: 前缀）");
            context.Reply("» 清除：!buffcookie clear");
            context.Reply("» 取法：浏览器登录 buff.163.com → F12 → Network → 任一请求 → Request Headers 的 Cookie");
            return;
        }

        if (raw.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            client.SetCookie(string.Empty);
            PersistCookie(string.Empty);
            _pendingAssetId = null;
            context.Reply("» Cookie 已清除");
            return;
        }

        var cookie = BuffClient.NormalizeCookie(raw);
        if (cookie.Length == 0)
        {
            context.Reply("» 这不像 Cookie：至少要包含 session=...");
            return;
        }

        client.SetCookie(cookie);
        PersistCookie(cookie);

        var assetId = _pendingAssetId;
        if (assetId is null)
        {
            context.Reply("» Cookie 已保存并写入 cookie.txt，下次 !buff 生效");
            return;
        }

        // 拿刚才失败的那一件当场验证，省得玩家再试一次才知道行不行
        _ = Task.Run(async () =>
        {
            var ok = await client.ValidateAsync(assetId, CancellationToken.None).ConfigureAwait(false);

            Core.Scheduler.NextTick(() =>
            {
                if (ok)
                {
                    _pendingAssetId = null;
                    context.Reply($"» Cookie 验证通过（assetid={assetId}），重新发一次 !buff 即可");
                }
                else
                {
                    context.Reply("» Cookie 已保存，但验证没过：可能已过期，或被 IP 风控");
                }
            });
        });
    }

    // ---------------------------------------------------------------- sw_bbtest

    /// <summary>
    /// 服务器端自检：不需要任何玩家在线，只跑「链接 → assetid → 检视解码」。
    ///
    ///   控制台：sw_bbtest              查看状态
    ///           sw_bbtest &lt;链接&gt;      跑一次解析
    /// </summary>
    [Command("bbtest")]
    public void OnTestCommand(ICommandContext context)
    {
        var client = _client;
        if (client is null)
        {
            context.Reply("» BuffBridge 还没加载完成");
            return;
        }

        var argument = string.Join(" ", context.Args).Trim();

        if (argument.Length == 0)
        {
            context.Reply("» BuffBridge " + PluginVersion);
            context.Reply("» 构建时间：" + BuildStamp);
            context.Reply("» WeaponSkins API：" + (_skins is null ? "未注入（WeaponSkins 没加载，或版本不匹配）" : "已注入"));
            context.Reply("» Cookie：" + (client.HasCookie ? "已配置" : "未配置"));
            context.Reply("» 代理：" + (_proxyInfo.Length == 0 ? "未设置" : _proxyInfo));
            context.Reply("» 用法：sw_bbtest <分享码 | 链接 | assetid>");
            return;
        }

        context.Reply("» 开始解析：" + argument);

        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;

            try
            {
                var item = await client.ResolveAsync(argument, CancellationToken.None).ConfigureAwait(false);
                var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;

                Core.Scheduler.NextTick(() =>
                {
                    context.Reply($"» 解析成功，{elapsed} ms");
                    context.Reply(
                        $"» defindex {item.DefinitionIndex} | 涂装 {item.PaintIndex} | 模板 {item.PaintSeed} | 磨损 {item.PaintWear.ToString("F10", CultureInfo.InvariantCulture)}");

                    if (item.Stickers.Count == 0)
                    {
                        context.Reply("» 印花 无");
                    }
                    else
                    {
                        var stickers = string.Join(", ", item.Stickers.Select(s => $"slot{s.Slot}=id{s.Id}"));
                        context.Reply($"» 印花 {item.Stickers.Count} 张：{stickers}");
                    }

                    context.Reply(
                        $"» 挂件 {(item.Keychain is null ? "无" : item.Keychain.Id.ToString())} | 改名 {item.NameTag ?? "无"}");
                    context.Reply("» 数据路由：" + KindName(Classify(item.DefinitionIndex)) + "（真正写入需要玩家在线）");
                });
            }
            catch (BuffException ex)
            {
                Core.Scheduler.NextTick(() =>
                {
                    context.Reply("» 解析失败：" + ex.Message);

                    if (ex.Kind is BuffError.NoCookie or BuffError.CookieRejected)
                    {
                        context.Reply("» 更新 Cookie：控制台 sw_buffcookie session=...（聊天里是 !buffcookie）");
                    }
                    else if (ex.Kind == BuffError.Forbidden)
                    {
                        context.Reply("» 403 是出口 IP 被风控：在 config.json 配 Proxy，换一条能出去的线路");
                    }
                });
            }
            catch (Exception ex)
            {
                Core.Scheduler.NextTick(() => context.Reply("» 解析失败：" + ex.Message));
            }
        });
    }

    private static string KindName(SkinKind kind) => kind switch
    {
        SkinKind.Knife => "刀",
        SkinKind.Glove => "手套",
        _ => "武器",
    };

    private void PersistCookie(string cookie)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cookie))
            {
                if (File.Exists(_cookieFile))
                {
                    File.Delete(_cookieFile);
                }

                return;
            }

            File.WriteAllText(_cookieFile, cookie);
        }
        catch
        {
            // 写不进去只影响下次重启，不影响本次运行
        }
    }

    // ---------------------------------------------------------------- 应用

    private void ApplyAndReport(ICommandContext context, IWeaponSkinAPI api, BuffItem item)
    {
        var player = context.Sender;
        if (player is null || !player.IsValid)
        {
            return;
        }

        if (item.DefinitionIndex == 0)
        {
            context.Reply("» 数据里没有武器编号（defindex），这件没法应用");
            return;
        }

        var steamId = player.Controller.SteamID;
        var team = player.Controller.Team;
        var definitionIndex = item.DefinitionIndex;
        var kind = Classify(definitionIndex);

        switch (kind)
        {
            case SkinKind.Knife:
                api.UpdateKnifeSkin(steamId, team, skin =>
                {
                    skin.DefinitionIndex = definitionIndex;
                    skin.Paintkit = item.PaintIndex;
                    skin.PaintkitSeed = item.PaintSeed;
                    skin.PaintkitWear = item.PaintWear;
                    if (item.IsStatTrak)
                    {
                        skin.Quality = EconItemQuality.Strange;
                    }

                    if (item.NameTag is not null)
                    {
                        skin.Nametag = item.NameTag;
                    }
                }, _persistSkins);
                break;

            case SkinKind.Glove:
                api.UpdateGloveSkin(steamId, team, skin =>
                {
                    skin.DefinitionIndex = definitionIndex;
                    skin.Paintkit = item.PaintIndex;
                    skin.PaintkitSeed = item.PaintSeed;
                    skin.PaintkitWear = item.PaintWear;
                }, _persistSkins);
                break;

            default:
                api.UpdateWeaponSkin(steamId, team, definitionIndex, skin =>
                {
                    skin.Paintkit = item.PaintIndex;
                    skin.PaintkitSeed = item.PaintSeed;
                    skin.PaintkitWear = item.PaintWear;
                    if (item.IsStatTrak)
                    {
                        skin.Quality = EconItemQuality.Strange;
                    }

                    if (item.NameTag is not null)
                    {
                        skin.Nametag = item.NameTag;
                    }

                    ApplyStickers(api, skin, item);
                    ApplyKeychain(api, skin, item);
                }, _persistSkins);
                break;
        }

        // 数据改了，但手上那把枪的实体不会自动重建——不复刷就得重新购买才看得到
        if (_refreshAfterApply)
        {
            TryRefreshPlayer(api, steamId, kind == SkinKind.Glove);
        }

        context.Reply($"» 已装备 {Describe(api, item)}");
        context.Reply(
            $"» 模板 {item.PaintSeed} | 磨损 {item.PaintWear.ToString("F10", CultureInfo.InvariantCulture)} | 印花 {item.Stickers.Count} 张");
    }

    /// <summary>
    /// WeaponSkins 只导出了 IWeaponSkinAPI，但内部 IInventoryUpdateService.RefreshPlayer
    /// 才是「重建手上武器」的入口。用反射取它，取不到就静默跳过（不影响换肤，只是要重新购买才看到）。
    /// </summary>
    private static void TryRefreshPlayer(IWeaponSkinAPI api, ulong steamId, bool resetGlove)
    {
        try
        {
            var property = api.GetType().GetProperty(
                "InventoryUpdateService",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var service = property?.GetValue(api);

            // 属性可能被改成字段/自动属性，兜底找一下 backing field
            service ??= api.GetType()
                .GetField("<InventoryUpdateService>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(api);

            if (service is null)
            {
                return;
            }

            var method = service.GetType().GetMethod(
                "RefreshPlayer",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                [typeof(ulong), typeof(bool)],
                null);

            method?.Invoke(service, [steamId, resetGlove]);
        }
        catch
        {
            // 反射失败只影响「立即生效」，换肤数据已经写进去了
        }
    }

    private static void ApplyStickers(IWeaponSkinAPI api, WeaponSkinData skin, BuffItem item)
    {
        // 先清空，避免旧印花残留在本次没覆盖到的槽位上
        for (var slot = 0; slot <= 5; slot++)
        {
            skin.SetSticker(slot, null);
        }

        foreach (var sticker in item.Stickers)
        {
            var id = ResolveStickerId(api, sticker);
            if (id <= 0)
            {
                continue;
            }

            skin.SetSticker(sticker.Slot, new StickerData
            {
                Id = id,
                Wear = sticker.Wear,
                Scale = sticker.Scale,
                OffsetX = sticker.OffsetX,
                OffsetY = sticker.OffsetY,
                Rotation = sticker.Rotation,
            });
        }
    }

    private static void ApplyKeychain(IWeaponSkinAPI api, WeaponSkinData skin, BuffItem item)
    {
        skin.SetKeychain(0, null);

        if (item.Keychain is null)
        {
            return;
        }

        var id = ResolveKeychainId(api, item.Keychain);
        if (id <= 0)
        {
            return;
        }

        skin.SetKeychain(item.Keychain.Slot, new KeychainData
        {
            Id = id,
            Seed = item.Keychain.Seed,
            OffsetX = item.Keychain.OffsetX,
            OffsetY = item.Keychain.OffsetY,
            OffsetZ = item.Keychain.OffsetZ,
        });
    }

    /// <summary>检视链接里的 sticker_id 正常就是游戏内印花 id；为 0 时按中文名回查。</summary>
    private static int ResolveStickerId(IWeaponSkinAPI api, BuffSticker sticker)
    {
        if (sticker.Id > 0)
        {
            return sticker.Id;
        }

        foreach (var collection in api.StickerCollections.Values)
        {
            foreach (var definition in collection.Stickers)
            {
                if (definition.LocalizedNames.TryGetValue("schinese", out var name)
                    && string.Equals(name, sticker.Name, StringComparison.Ordinal))
                {
                    return definition.Index;
                }
            }
        }

        return 0;
    }

    private static int ResolveKeychainId(IWeaponSkinAPI api, BuffKeychain keychain)
    {
        if (keychain.Id > 0)
        {
            return keychain.Id;
        }

        foreach (var definition in api.Keychains.Values)
        {
            if (definition.LocalizedNames.TryGetValue("schinese", out var name)
                && string.Equals(name, keychain.Name, StringComparison.Ordinal))
            {
                return definition.Index;
            }
        }

        return 0;
    }

    private static SkinKind Classify(ushort definitionIndex) => definitionIndex switch
    {
        >= 500 and <= 526 => SkinKind.Knife,
        >= 4725 => SkinKind.Glove,
        _ => SkinKind.Weapon,
    };

    /// <summary>检视数据没有饰品名，用 ItemDefinition 的中文名 + 涂装号拼一个。</summary>
    private static string Describe(IWeaponSkinAPI api, BuffItem item)
    {
        foreach (var definition in api.Items.Values)
        {
            if (definition.Index != item.DefinitionIndex)
            {
                continue;
            }

            var weaponName = definition.LocalizedNames.TryGetValue("schinese", out var localized)
                ? localized
                : definition.Name;

            return $"{weaponName}（涂装 {item.PaintIndex}）";
        }

        return $"defindex {item.DefinitionIndex}（涂装 {item.PaintIndex}）";
    }
}
