#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BUFF 解析探针：验证「能不能从一条分享链接解析出饰品」，不连游戏、不碰 WeaponSkins。

注意：插件本身只用「检视链接」这一条路；这里的分享页/挂单只是对照用的诊断手段。

三件事按顺序做：
  1. 跟 302，拿到 goods_id / assetid
  2. 抓分享页，从 HTML 里挖 `var items`，按 assetid 找那一件（诊断用）
  3. （可选，给了 --cookie）调 /api/market/cs2_inspect，解码 CEconItemPreviewDataBlock

只用标准库，不需要 pip。

用法：
  python tools/buff_probe.py "https://buff.163.com/market/s/xEcyB2W4"
  python tools/buff_probe.py "https://buff.163.com/market/s/xEcyB2W4" --proxy http://127.0.0.1:7890
  python tools/buff_probe.py 54129164852 --cookie "session=<your-token>" --proxy http://127.0.0.1:7890
"""

import argparse
import gzip
import json
import re
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# Windows 控制台默认 cp936，强制 UTF-8 免得中文变乱码
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
)

GOODS_RE = re.compile(r"/goods/(\d+)", re.I)
ASSET_RE = re.compile(r"[?&]assetid=(\d+)", re.I)
BARE_ID_RE = re.compile(r"^\d{4,}$")
PREVIEW_MARKER = "csgo_econ_action_preview"

# 给 GUI 复用：最近一次解析出来的结构化结果
LAST_SHARE = None
LAST_INSPECT = None


# --------------------------------------------------------------------------- HTTP


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # 不跟随，交给调用方读 Location


def build_opener(proxy, cookie):
    handlers = [NoRedirect()]
    if proxy:
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))

    opener = urllib.request.build_opener(*handlers)
    opener.addheaders = [
        ("User-Agent", UA),
        ("Accept", "text/html,application/json,*/*"),
        ("Accept-Language", "zh-CN,zh;q=0.9"),
        ("Accept-Encoding", "identity"),
    ]
    if cookie:
        opener.addheaders.append(("Cookie", cookie))
    return opener


def fetch(opener, url):
    """返回 (status, headers, body_bytes)，不抛 HTTPError。"""
    try:
        resp = opener.open(url, timeout=30)
        return resp.status, resp.headers, resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.headers, exc.read()
    except urllib.error.URLError as exc:
        raise SystemExit(f"[网络] 请求失败：{exc.reason}") from None
    except TimeoutError:
        raise SystemExit("[网络] 请求超时") from None


def body_text(headers, body):
    if (headers.get("Content-Encoding") or "").lower() == "gzip":
        try:
            body = gzip.decompress(body)
        except OSError:
            pass
    return body.decode("utf-8", "replace")


# --------------------------------------------------------------------------- 1. 302


def resolve_target(opener, link):
    if BARE_ID_RE.match(link):
        link = "https://buff.163.com/goods/" + link

    if not link.startswith("http"):
        link = "https://" + link

    if "buff.163.com" not in link:
        raise SystemExit("[输入] 这不是 buff.163.com 的链接")

    print(f"[1/3] 解析链接  {link}")

    parsed = urllib.parse.urlsplit(link)
    direct = GOODS_RE.search(parsed.path)

    if direct:
        asset = ASSET_RE.search(parsed.query)
        goods_id = direct.group(1)
        asset_id = asset.group(1) if asset else None
        print(f"      URL 里直接有 goods_id={goods_id}  assetid={asset_id}")
        return link, goods_id, asset_id

    status, headers, _ = fetch(opener, link)
    location = headers.get("Location")

    if status in (301, 302, 303, 307, 308) and location:
        location = urllib.parse.urljoin(link, location)
        print(f"      302 → {location}")

        if "/account/login" in location:
            raise SystemExit(
                "[失败] 这个链接要求登录（被重定向到 /account/login）。\n"
                "       这条路径需要 Cookie：加 --cookie \"session=...\" 再试。"
            )

        direct = GOODS_RE.search(urllib.parse.urlsplit(location).path)
        if not direct:
            raise SystemExit("[失败] 跳转目标里没有 /goods/{id}")

        asset = ASSET_RE.search(location)
        goods_id = direct.group(1)
        asset_id = asset.group(1) if asset else None
        print(f"      goods_id={goods_id}  assetid={asset_id}")
        return location, goods_id, asset_id

    if status == 403:
        raise SystemExit("[失败] 403 —— 出口 IP 被风控。加 --proxy 换出口再试。")

    if status == 200:
        raise SystemExit(
            "[失败] 没有 302，直接返回了页面。可能是链接已经失效，或者 BUFF 改了跳转逻辑。"
        )

    raise SystemExit(f"[失败] 意外状态码 {status}")


# --------------------------------------------------------------------------- 2. var items


def iter_items_literals(html):
    search_from = 0
    while True:
        marker = html.find("var items", search_from)
        if marker < 0:
            return
        search_from = marker + len("var items")

        equals = html.find("=", marker)
        if equals < 0:
            continue

        start = -1
        for i in range(equals + 1, len(html)):
            ch = html[i]
            if ch in "[{":
                start = i
                break
            if not ch.isspace():
                break

        if start < 0:
            continue

        depth = 0
        in_string = False
        escaped = False

        for i in range(start, len(html)):
            ch = html[i]
            if in_string:
                if escaped:
                    escaped = False
                elif ch == "\\":
                    escaped = True
                elif ch == '"':
                    in_string = False
                continue
            if ch == '"':
                in_string = True
                continue
            if ch in "[{":
                depth += 1
            elif ch in "]}":
                depth -= 1
                if depth == 0:
                    yield html[start:i + 1]
                    break


def find_item(root, asset_id):
    if isinstance(root, list):
        if not root:
            return None
        if asset_id is None:
            return root[0]
        for element in root:
            if asset_matches(element, asset_id):
                return element
        return None

    if isinstance(root, dict):
        for value in root.values():
            if asset_id is None or asset_matches(value, asset_id):
                return value

    return None


def asset_matches(element, asset_id):
    if not isinstance(element, dict):
        return False
    asset_info = element.get("asset_info")
    if not isinstance(asset_info, dict):
        return False
    return str(asset_info.get("assetid", "")) == str(asset_id)


def dig(obj, *keys):
    """在 obj 以及它的 info 子对象里找第一个存在的 key。"""
    if not isinstance(obj, dict):
        return None
    for key in keys:
        if key in obj:
            return obj[key]
    info = obj.get("info")
    if isinstance(info, dict):
        for key in keys:
            if key in info:
                return info[key]
    return None


def pct(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def parse_share_page(opener, page_url, asset_id):
    print(f"[2/3] 抓分享页  {page_url}")

    status, headers, body = fetch(opener, page_url)
    print(f"      HTTP {status}  {len(body)} bytes")

    if status == 403:
        print("      ✗ 403，出口 IP 被风控")
        return False
    if status != 200:
        print(f"      ✗ 状态码异常：{status}")
        return False

    html = body_text(headers, body)
    total = html.count("var items")
    print(f"      var items 出现 {total} 次")

    if total == 0:
        print("      ✗ 页面里没有 var items（可能被换成了别的变量名，或者只渲染给登录用户）")
        return False

    for index, literal in enumerate(iter_items_literals(html), start=1):
        try:
            root = json.loads(literal)
        except json.JSONDecodeError as exc:
            print(f"      · 第 {index} 段不是合法 JSON：{exc}")
            continue

        item = find_item(root, asset_id)
        if item is None:
            print(f"      · 第 {index} 段 JSON 里没有 assetid={asset_id}")
            continue

        asset_info = item.get("asset_info") or {}
        goods_info = item.get("goods_info") or {}

        name = goods_info.get("short_name") or goods_info.get("name") or "(无名称)"
        tags = goods_info.get("tags") or {}
        weapon = (tags.get("weapon") or {}).get("internal_name", "?")
        quality = (tags.get("quality") or {}).get("internal_name", "?")

        paint = dig(asset_info, "paintindex")
        seed = dig(asset_info, "paintseed")
        wear = dig(asset_info, "paintwear")
        stickers = dig(asset_info, "stickers") or []
        keychains = dig(asset_info, "keychains") or []

        print(f"      ✓ 命中 assetid={asset_info.get('assetid')}")
        print(f"        名称    {name}")
        print(f"        武器    {weapon}   品质 {quality}")
        print(f"        涂装    {paint}   模板 {seed}   磨损 {wear}")
        print(f"        印花    {len(stickers)} 张")

        for sticker in stickers:
            print(
                "          - slot {slot}  id {sid}  wear {wear:.3f}  "
                "offset ({x:+.4f}, {y:+.4f})  rot {rot:.1f}  {name}".format(
                    slot=sticker.get("slot"),
                    sid=sticker.get("sticker_id"),
                    wear=pct(sticker.get("wear")),
                    x=pct(sticker.get("offset_x")),
                    y=pct(sticker.get("offset_y")),
                    rot=pct(sticker.get("rotation")),
                    name=sticker.get("name", ""),
                )
            )

        print(f"        挂件    {len(keychains)} 个")
        for keychain in keychains:
            print(
                "          - slot {slot}  id {kid}  seed {seed}  {name}".format(
                    slot=keychain.get("slot"),
                    kid=keychain.get("keychain_id") or keychain.get("id"),
                    seed=keychain.get("seed"),
                    name=keychain.get("name", ""),
                )
            )

        global LAST_SHARE
        LAST_SHARE = {
            "asset_id": asset_info.get("assetid"),
            "name": name,
            "weapon": weapon,
            "quality": quality,
            "paintindex": paint,
            "paintseed": seed,
            "paintwear": wear,
            "stickers": stickers,
            "keychains": keychains,
        }

        return True

    print("      ✗ 所有 var items 段都没命中")
    return False


# --------------------------------------------------------------------------- 3. cs2_inspect


def read_varint(buf, index):
    shift = 0
    value = 0
    while index < len(buf):
        current = buf[index]
        index += 1
        value |= (current & 0x7F) << shift
        if not current & 0x80:
            return value, index
        shift += 7
    raise ValueError("varint 越界")


def parse_pattern_message(data):
    out = {"slot": 0, "id": 0, "wear": 0.0, "scale": 0.0, "rotation": 0.0,
           "x": 0.0, "y": 0.0, "z": 0.0, "seed": 0}
    index = 0
    try:
        while index < len(data):
            key, index = read_varint(data, index)
            field, wire = key >> 3, key & 7

            if wire == 0:
                value, index = read_varint(data, index)
                if field == 1:
                    out["slot"] = value
                elif field == 2:
                    out["id"] = value
                elif field == 10:
                    out["seed"] = value
            elif wire == 5:
                if index + 4 > len(data):
                    break
                number = struct.unpack("<f", data[index:index + 4])[0]
                index += 4
                if field == 3:
                    out["wear"] = number
                elif field == 4:
                    out["scale"] = number
                elif field == 5:
                    out["rotation"] = number
                elif field == 7:
                    out["x"] = number
                elif field == 8:
                    out["y"] = number
                elif field == 9:
                    out["z"] = number
            elif wire == 2:
                length, index = read_varint(data, index)
                if index + length > len(data):
                    break
                index += length
            elif wire == 1:
                index += 8
            else:
                break
    except (ValueError, IndexError):
        pass

    if out["scale"] <= 0:
        out["scale"] = 1.0  # 字段缺省时游戏侧就是 1
    return out


def decode_preview(inspect_url):
    text = urllib.parse.unquote(inspect_url)
    marker = text.lower().find(PREVIEW_MARKER)
    if marker >= 0:
        text = text[marker + len(PREVIEW_MARKER):]

    hexstr = "".join(ch for ch in text if ch in "0123456789abcdefABCDEF")
    data = bytes.fromhex(hexstr)
    if data and data[0] == 0:
        data = data[1:]

    result = {"defindex": 0, "paintindex": 0, "paintseed": 0, "paintwear": 0.0,
              "quality": 0, "stickers": [], "keychains": [], "name": None}
    index = 0
    seen = set()

    try:
        while index < len(data):
            key, index = read_varint(data, index)
            field, wire = key >> 3, key & 7

            if wire == 0:
                value, index = read_varint(data, index)

                # 标量字段只认第一次出现。BUFF 返回的载荷尾部有时会多 4 字节，
                # 会被误读成"第二个 defindex"（样例里是 6719），从而把武器判成手套。
                if field in seen:
                    continue

                if field == 3:
                    seen.add(field)
                    result["defindex"] = value
                elif field == 4:
                    seen.add(field)
                    result["paintindex"] = value
                elif field == 6:
                    seen.add(field)
                    result["quality"] = value
                elif field == 7:
                    seen.add(field)
                    result["paintwear"] = struct.unpack("<f", struct.pack("<I", value))[0]
                elif field == 8:
                    seen.add(field)
                    result["paintseed"] = value
            elif wire == 2:
                length, index = read_varint(data, index)
                if index + length > len(data):
                    break
                payload = data[index:index + length]
                index += length
                if field == 11:
                    result["name"] = payload.decode("utf-8", "replace")
                elif field == 12:
                    sticker = parse_pattern_message(payload)
                    if sticker["id"]:
                        result["stickers"].append(sticker)
                elif field == 20:
                    keychain = parse_pattern_message(payload)
                    if keychain["id"]:
                        result["keychains"].append(keychain)
            elif wire == 5:
                if index + 4 > len(data):
                    break
                index += 4
            elif wire == 1:
                index += 8
            else:
                break
    except (ValueError, IndexError):
        pass

    return result


def probe_inspect(opener, asset_id):
    print(f"[3/3] 检视链接  assetid={asset_id}")

    url = (
        f"https://buff.163.com/api/market/cs2_inspect"
        f"?assetid={asset_id}&_={int(time.time() * 1000)}"
    )
    status, headers, body = fetch(opener, url)

    if status in (301, 302, 303, 307, 308):
        location = headers.get("Location", "")
        if "/account/login" in location:
            print("      ✗ 302 → /account/login：这个接口要登录态，--cookie 没给或已过期")
        else:
            print(f"      ✗ 302 → {location}")
        return False

    if status == 403:
        print("      ✗ 403，出口 IP 被风控")
        return False

    if status != 200:
        print(f"      ✗ 状态码 {status}")
        return False

    try:
        payload = json.loads(body.decode("utf-8", "replace"))
    except json.JSONDecodeError:
        print("      ✗ 返回的不是 JSON")
        return False

    if payload.get("code") != "OK":
        print(f"      ✗ code={payload.get('code')}  msg={payload.get('msg')}")
        return False

    data = payload.get("data") or ""
    if not data.startswith("steam://"):
        print(f"      ✗ data 不是 steam:// 链接：{data[:80]}")
        return False

    try:
        decoded = decode_preview(data)
    except Exception as exc:  # noqa: BLE001
        print(f"      ✗ 解码失败：{type(exc).__name__}: {exc}")
        return False

    if not decoded["paintindex"] or not decoded["defindex"]:
        print("      ✗ 解出来是空数据（paintindex/defindex 为 0）")
        return False

    global LAST_INSPECT
    LAST_INSPECT = decoded

    print("      ✓ 解码成功")
    print(f"        defindex {decoded['defindex']}   涂装 {decoded['paintindex']}   "
          f"模板 {decoded['paintseed']}   磨损 {decoded['paintwear']:.10f}")
    if decoded["name"]:
        print(f"        改名     {decoded['name']}")
    print(f"        印花     {len(decoded['stickers'])} 张")
    for sticker in decoded["stickers"]:
        print(
            "          - slot {slot}  id {sid}  wear {wear:.3f}  scale {scale:.3f}  "
            "offset ({x:+.5f}, {y:+.5f}, {z:+.5f})  rot {rot:.1f}".format(
                slot=sticker["slot"], sid=sticker["id"], wear=sticker["wear"],
                scale=sticker["scale"], x=sticker["x"], y=sticker["y"],
                z=sticker["z"], rot=sticker["rotation"],
            )
        )
    print(f"        挂件     {len(decoded['keychains'])} 个")
    for keychain in decoded["keychains"]:
        print(
            "          - slot {slot}  id {kid}  seed {seed}  "
            "offset ({x:+.5f}, {y:+.5f}, {z:+.5f})".format(
                slot=keychain["slot"], kid=keychain["id"], seed=keychain["seed"],
                x=keychain["x"], y=keychain["y"], z=keychain["z"],
            )
        )
    return True


# --------------------------------------------------------------------------- main


def main():
    parser = argparse.ArgumentParser(description="验证 BUFF 分享链接能否解析出饰品")
    parser.add_argument("link", nargs="?", default="", help="分享链接 / goods 链接 / 纯 goods_id")
    parser.add_argument("--asset-id", default="", help="直接指定 assetid（跳过 302，只测检视解码）")
    parser.add_argument("--cookie", default="", help="登录 Cookie，整串粘贴，形如 session=<token>")
    parser.add_argument("--proxy", default="", help="HTTP 代理，如 http://127.0.0.1:7890")
    parser.add_argument("--skip-page", action="store_true", help="跳过分享页，只测检视链接")
    parser.add_argument("--skip-inspect", action="store_true", help="跳过检视链接，只测分享页")
    args = parser.parse_args()

    if not args.link.strip() and not args.asset_id.strip():
        parser.error("要么给一个链接，要么用 --asset-id 指定资产 id")

    opener = build_opener(args.proxy.strip(), args.cookie.strip())

    if args.proxy.strip():
        print(f"[配置] 代理 {args.proxy}")
    print(f"[配置] Cookie {'已提供（%d 字符）' % len(args.cookie) if args.cookie else '未提供'}")

    if args.asset_id.strip():
        page_url, goods_id, asset_id = None, None, args.asset_id.strip()
        print(f"[1/3] 指定 assetid={asset_id}（跳过 302）")
    else:
        page_url, goods_id, asset_id = resolve_target(opener, args.link.strip())

    ok_page = None
    ok_inspect = None

    if not args.skip_page and page_url:
        ok_page = parse_share_page(opener, page_url, asset_id)
    else:
        print("[2/3] 跳过分享页")

    if not args.skip_inspect and asset_id:
        ok_inspect = probe_inspect(opener, asset_id)
    elif not args.skip_inspect:
        print("[3/3] 跳过检视链接（没有 assetid）")

    print()
    print("=" * 62)
    print(f"分享页解析 : {'跳过' if ok_page is None else ('成功' if ok_page else '失败')}")
    print(f"检视解码   : {'跳过' if ok_inspect is None else ('成功' if ok_inspect else '未成功')}")

    if ok_page or ok_inspect:
        print("结论：能拿到饰品数据，插件这条链路是通的。")
        return 0

    print("结论：两条路都没拿到。按上面的报错处理（403 → 换出口；login → 补 Cookie）。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
