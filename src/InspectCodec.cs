using System.Text;

namespace BuffBridge;

internal sealed record DecodedInspect(
    int DefinitionIndex,
    int PaintIndex,
    int PaintSeed,
    float PaintWear,
    int Quality,
    int Rarity,
    string? NameTag,
    IReadOnlyList<BuffSticker> Stickers,
    BuffKeychain? Keychain)
{
    /// <summary>items_game 里 quality 9 = strange（StatTrak）。</summary>
    public bool IsStatTrak => Quality == 9;
}

/// <summary>
/// 解码 BUFF "在游戏中检视" 返回的 steam:// 链接。
///
/// 链接里的载荷是 <c>+csgo_econ_action_preview &lt;hex&gt;</c>，hex 前面有一个 0x00 前缀字节，
/// 后面是 protobuf <c>CEconItemPreviewDataBlock</c>：
///
///  3 defindex, 4 paintindex, 5 rarity, 6 quality,
///  7 paintwear(uint32 → float 位模式), 8 paintseed, 11 customname,
///  12 stickers[], 20 keychains[]
///
/// 贴纸子消息：1 slot, 2 sticker_id, 3 wear(float), 4 scale, 5 rotation,
///             7 offset_x, 8 offset_y, 9 offset_z, 10 pattern(挂件当 seed)
/// </summary>
internal static class InspectCodec
{
    private const string PreviewMarker = "csgo_econ_action_preview";

    public static DecodedInspect? Decode(string inspectUrl)
    {
        var hex = ExtractHex(inspectUrl);
        if (hex.Length < 2 || hex.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }

        // 载荷前的 0x00 前缀不是 protobuf 字段
        var index = bytes.Length > 0 && bytes[0] == 0 ? 1 : 0;

        var definitionIndex = 0;
        var paintIndex = 0;
        var paintSeed = 0;
        var quality = 0;
        var rarity = 0;
        var paintWear = 0f;
        string? nameTag = null;
        var stickers = new List<BuffSticker>();
        BuffKeychain? keychain = null;

        // 标量字段只认第一次出现：BUFF 的载荷尾部偶尔多 4 字节，
        // 会被误读成"第二个 defindex"，把武器判成手套。
        var hasDefinitionIndex = false;
        var hasPaintIndex = false;
        var hasPaintSeed = false;
        var hasQuality = false;
        var hasRarity = false;
        var hasWear = false;

        while (index < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref index, out var key))
            {
                break;
            }

            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(bytes, ref index, out var value))
                    {
                        return Finish();
                    }

                    switch (field)
                    {
                        case 3 when !hasDefinitionIndex:
                            hasDefinitionIndex = true;
                            definitionIndex = (int)value;
                            break;
                        case 4 when !hasPaintIndex:
                            hasPaintIndex = true;
                            paintIndex = (int)value;
                            break;
                        case 5 when !hasRarity:
                            hasRarity = true;
                            rarity = (int)value;
                            break;
                        case 6 when !hasQuality:
                            hasQuality = true;
                            quality = (int)value;
                            break;
                        case 7 when !hasWear:
                            hasWear = true;
                            paintWear = BitConverter.Int32BitsToSingle((int)value);
                            break;
                        case 8 when !hasPaintSeed:
                            hasPaintSeed = true;
                            paintSeed = (int)value;
                            break;
                    }

                    break;

                case 2:
                    if (!TryReadVarint(bytes, ref index, out var length))
                    {
                        return Finish();
                    }

                    if (length > (ulong)(bytes.Length - index))
                    {
                        return Finish();
                    }

                    var payload = bytes.AsSpan(index, (int)length);
                    index += (int)length;

                    if (field == 11)
                    {
                        nameTag = Encoding.UTF8.GetString(payload);
                    }
                    else if (field == 12)
                    {
                        var sticker = ParsePatternMessage(payload);
                        if (sticker.Id > 0)
                        {
                            stickers.Add(new BuffSticker(
                                sticker.Id,
                                sticker.Slot,
                                sticker.Wear,
                                sticker.X,
                                sticker.Y,
                                sticker.Rotation,
                                string.Empty)
                            {
                                Scale = sticker.Scale,
                            });
                        }
                    }
                    else if (field == 20 && keychain is null)
                    {
                        var decoded = ParsePatternMessage(payload);
                        if (decoded.Id > 0)
                        {
                            keychain = new BuffKeychain(
                                decoded.Id,
                                decoded.Slot,
                                decoded.Seed,
                                decoded.X,
                                decoded.Y,
                                decoded.Z,
                                string.Empty);
                        }
                    }

                    break;

                case 5:
                    // 顶层没有已知 float 字段，跳过 4 字节
                    if (index + 4 > bytes.Length)
                    {
                        return Finish();
                    }

                    index += 4;
                    break;

                case 1:
                    if (index + 8 > bytes.Length)
                    {
                        return Finish();
                    }

                    index += 8;
                    break;

                default:
                    // 遇到不认识的 wire type 就收工，已经读到的字段照样用
                    return Finish();
            }
        }

        return Finish();

        DecodedInspect? Finish()
        {
            if (paintIndex <= 0 || definitionIndex <= 0)
            {
                return null;
            }

            return new DecodedInspect(
                definitionIndex,
                paintIndex,
                paintSeed,
                paintWear,
                quality,
                rarity,
                string.IsNullOrWhiteSpace(nameTag) ? null : nameTag,
                stickers,
                keychain);
        }
    }

    private readonly record struct PatternMessage(
        int Slot,
        int Id,
        float Wear,
        float Scale,
        float Rotation,
        float X,
        float Y,
        float Z,
        int Seed);

    private static PatternMessage ParsePatternMessage(ReadOnlySpan<byte> data)
    {
        var slot = 0;
        var id = 0;
        var seed = 0;
        var wear = 0f;
        var scale = 0f;
        var rotation = 0f;
        var x = 0f;
        var y = 0f;
        var z = 0f;

        var index = 0;

        while (index < data.Length)
        {
            if (!TryReadVarint(data, ref index, out var key))
            {
                break;
            }

            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            if (wire == 0)
            {
                if (!TryReadVarint(data, ref index, out var value))
                {
                    break;
                }

                switch (field)
                {
                    case 1: slot = (int)value; break;
                    case 2: id = (int)value; break;
                    case 10: seed = (int)value; break;
                }
            }
            else if (wire == 5)
            {
                if (index + 4 > data.Length)
                {
                    break;
                }

                var number = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(data[index..]));
                index += 4;

                switch (field)
                {
                    case 3: wear = number; break;
                    case 4: scale = number; break;
                    case 5: rotation = number; break;
                    case 7: x = number; break;
                    case 8: y = number; break;
                    case 9: z = number; break;
                }
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(data, ref index, out var length) || length > (ulong)(data.Length - index))
                {
                    break;
                }

                index += (int)length;
            }
            else if (wire == 1)
            {
                if (index + 8 > data.Length)
                {
                    break;
                }

                index += 8;
            }
            else
            {
                break;
            }
        }

        return new PatternMessage(slot, id, wear, scale <= 0f ? 1f : scale, rotation, x, y, z, seed);
    }

    private static string ExtractHex(string inspectUrl)
    {
        var text = Uri.UnescapeDataString(inspectUrl);

        var marker = text.IndexOf(PreviewMarker, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            text = text[(marker + PreviewMarker.Length)..];
        }

        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int index, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (index < data.Length && shift < 64)
        {
            var current = data[index];
            index++;
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
