namespace BuffBridge;

internal enum SkinKind
{
    Weapon,
    Knife,
    Glove,
}

/// <summary>失败原因，决定给玩家什么提示。</summary>
internal enum BuffError
{
    /// <summary>链接本身有问题（没有 assetid）。</summary>
    InvalidLink,

    /// <summary>还没配 Cookie。</summary>
    NoCookie,

    /// <summary>Cookie 过期 / 被 BUFF 拒绝（302 到登录页）。</summary>
    CookieRejected,

    /// <summary>403：IP 被风控，换 Cookie 没用。</summary>
    Forbidden,

    /// <summary>请求太频繁。</summary>
    RateLimited,

    /// <summary>饰品不存在 / 已下架。</summary>
    NotFound,

    /// <summary>网络、解析等其它问题。</summary>
    Unknown,
}

/// <summary>BUFF asset_info.info.stickers[] 里的一条。</summary>
internal sealed record BuffSticker(
    int Id,
    int Slot,
    float Wear,
    float OffsetX,
    float OffsetY,
    float Rotation,
    string Name)
{
    /// <summary>贴纸缩放，检视链接的 field 4；缺省时游戏侧就是 1。</summary>
    public float Scale { get; init; } = 1f;
}

/// <summary>挂件（检视链接 field 20）。</summary>
internal sealed record BuffKeychain(
    int Id,
    int Slot,
    int Seed,
    float OffsetX,
    float OffsetY,
    float OffsetZ,
    string Name);

/// <summary>解析出来的一件饰品，跟 BUFF 与 WeaponSkins 都解耦。</summary>
internal sealed record BuffItem(
    int PaintIndex,
    int PaintSeed,
    float PaintWear,
    bool IsStatTrak,
    string? AssetId,
    ushort DefinitionIndex)
{
    public IReadOnlyList<BuffSticker> Stickers { get; init; } = [];

    public BuffKeychain? Keychain { get; init; }

    public string? NameTag { get; init; }
}

internal sealed class BuffException(BuffError kind, string message) : Exception(message)
{
    public BuffError Kind { get; } = kind;

    /// <summary>失败时正在处理的 assetid，用来在补 Cookie 后重新验证。</summary>
    public string? AssetId { get; init; }
}
