import struct

HEX = "001809208302280538BCD6BEF303400B620A080010EE261D00000000620A080110EE261D00000000620A080210EE261D0000000062140803108C341D000000003DB879BDBB45004B803A6214080410BA331D000000003D20622BBE4500246BBA517C1E5E"

data = bytes.fromhex(HEX)
print("total bytes:", len(data))
print("hex:", data.hex())

NAMES = {
    1: "accountid",
    2: "itemid",
    3: "defindex",
    4: "paintindex",
    5: "rarity",
    6: "quality",
    7: "paintwear",
    8: "paintseed",
    9: "killeaterscoretype",
    10: "killeatervalue",
    11: "customname",
    12: "stickers",
    13: "inventory",
    14: "origin",
    15: "questid",
    16: "dropreason",
    17: "musicindex",
    18: "entindex",
    19: "petindex",
    20: "keychains",
    21: "style",
}

STICKER_NAMES = {
    1: "slot",
    2: "sticker_id",
    3: "wear",
    4: "scale",
    5: "rotation",
    6: "tint_id",
    7: "offset_x",
    8: "offset_y",
    9: "offset_z",
    10: "pattern",
}


def read_varint(buf, i):
    shift = 0
    val = 0
    while True:
        if i >= len(buf):
            raise ValueError("varint overrun")
        b = buf[i]
        i += 1
        val |= (b & 0x7F) << shift
        if not b & 0x80:
            return val, i
        shift += 7


def walk(buf, names, indent, label):
    pad = "  " * indent
    i = 0
    print(f"{pad}--- {label} ({len(buf)} bytes) ---")
    while i < len(buf):
        start = i
        key, i = read_varint(buf, i)
        field, wt = key >> 3, key & 7
        name = names.get(field, f"?{field}")
        if wt == 0:
            v, i = read_varint(buf, i)
            print(f"{pad}{name} [{field}/varint @{start}] = {v}")
        elif wt == 2:
            ln, i = read_varint(buf, i)
            payload = buf[i:i + ln]
            i += ln
            if field in (12, 20) or (indent == 0 and name in ("stickers", "keychains")):
                walk(payload, STICKER_NAMES, indent + 1, f"{name}[{field}]")
            else:
                try:
                    print(f"{pad}{name} [{field}/bytes @{start}] = {payload.decode()!r}")
                except UnicodeDecodeError:
                    print(f"{pad}{name} [{field}/bytes @{start}] = {payload.hex()}")
        elif wt == 5:
            raw = buf[i:i + 4]
            if len(raw) != 4:
                print(f"{pad}!! fixed32 truncated at {i}, remaining {buf[i:].hex()}")
                return
            i += 4
            print(f"{pad}{name} [{field}/float @{start}] = {struct.unpack('<f', raw)[0]!r}  raw {raw.hex()}")
        elif wt == 1:
            raw = buf[i:i + 8]
            if len(raw) != 8:
                print(f"{pad}!! fixed64 truncated at {i}, remaining {buf[i:].hex()}")
                return
            i += 8
            print(f"{pad}{name} [{field}/double @{start}] = {struct.unpack('<d', raw)[0]!r}")
        else:
            print(f"{pad}!! bad wire type {wt} for field {field} at {start}")
            return


body = data[1:] if data[:1] == b"\x00" else data
print("starts with 0x00:", data[:1] == b"\x00")
walk(body, NAMES, 0, "CEconItemPreviewDataBlock")
