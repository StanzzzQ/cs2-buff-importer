# BuffBridge

CS2 服务器插件（SwiftlyS2 / .NET 10）：玩家在聊天框输入 `!buff <BUFF 分享码>`，
把自己当前的武器 / 刀 / 手套换成链接里那件饰品的外观——磨损、模板、印花（含位置/旋转/缩放）、
挂件、改名、StatTrak 一起带上。

数据只有一条来源：BUFF 的「在游戏中检视」接口。它返回一条 `steam://` 链接，里面的 hex 是
`CEconItemPreviewDataBlock`（protobuf），本插件自己解，不依赖任何 protobuf 库。

> 本项目与 BUFF、Valve、SwiftlyS2 官方均无关联，仅为自建服务器的娱乐用途。

---

## 快速开始（给服主）

### 1. 安装

下载 Release 里的 `BuffBridge.zip`，解压到：

```
<服务器>/game/csgo/addons/swiftlys2/plugins/BuffBridge/
```

然后重启服务器，或控制台 `sw plugins reload BuffBridge`。

前提：`plugins/WeaponSkins` 已经在跑（本插件通过它的共享接口改数据）。

### 2. 配一条 Cookie（唯一必须做的事）

**方式 A（推荐）**：进游戏聊天框发

```
!buffcookie session=<粘贴整串>
```

插件会写进 `cookie.txt`，重启不丢，也不会改动 `config.json`。

**方式 B**：直接编辑 `plugins/BuffBridge/config.json` 的 `Cookie` 字段。

Cookie 怎么取：浏览器登录 BUFF → F12 → Network → 刷新 → 任一 `buff.163.com` 请求 →
Request Headers 里的 `Cookie` → 整串复制。

> 没有 Cookie 也能装，只是玩家发 `!buff` 时会被提示去补——等于自带引导。
> 注意 `config.json` 和 `cookie.txt` 都是**明文**，建议用专用小号。

### 3. 验证

```
sw_bbtest                 # 插件版本 / WeaponSkins API / Cookie / 代理
sw_bbtest u86YQwb6        # 真跑一次解析（不会改任何人的皮肤）
```

### 4. 可选：代理、冷却、立即生效

`config.json`（不存在也能跑，用默认值）：

```json
{
  "Cookie": "",
  "Proxy": "http://127.0.0.1:7890",
  "CooldownSeconds": 3,
  "RefreshAfterApply": true
}
```

- `Proxy`：服务器出口 IP 被 BUFF 风控（报 403）时填
- `CooldownSeconds`：每玩家冷却秒数
- `RefreshAfterApply`：换完立刻重建手上的武器（默认 `true`）。关掉的话，玩家要重新购买/捡枪才会看到新皮肤

`cookie.txt` 的优先级高于 `config.json` 里的 `Cookie`。

---

## 特性

- **一条命令换整套外观**：涂装、模板、磨损、印花、挂件、改名、StatTrak
- **换完立即生效**：自动让 WeaponSkins 重建手上的武器，不用重新购买
- **不解析网页**：直接读游戏自己的资产数据（protobuf），比抓 HTML 稳
- **印花按游戏原生参数写入**：slot / wear / scale / rotation / offset x,y
- **Cookie 失效自动求助**：在聊天里让玩家现场贴一条新 Cookie，并当场验证
- **服务端自检**：`sw_bbtest` 不依赖任何玩家在线，能单独验证网络、Cookie、解码
- **编译不需要 NuGet**：直接引用服务器上的两个 DLL，版本对齐

## 依赖

| 依赖 | 说明 |
| --- | --- |
| [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2) | 插件运行框架，需要 .NET 10 |
| [WeaponSkins](https://github.com/samyycX/WeaponSkins) | 真正改游戏数据的一方，通过共享接口 `WeaponSkins.API` 调用 |
| BUFF 账号 Cookie | 「检视」接口需要登录态，未登录会 302 到登录页 |

## 工作原理

```
玩家: !buff u86YQwb6
        │
        ├─ 1. 302   https://buff.163.com/market/s/u86YQwb6
        │           → /goods/38408?...&assetid=54116611346
        │
        ├─ 2. GET   /api/market/cs2_inspect?assetid=...
        │           → "steam://run/730/en/+csgo_econ_action_preview%20001801..."
        │
        ├─ 3. 解码  00 + protobuf CEconItemPreviewDataBlock
        │           → defindex / paintindex / paintseed / paintwear
        │             stickers[]（slot, id, wear, scale, rotation, offset）
        │             keychains[] / customname / quality
        │
        └─ 4. 应用  IWeaponSkinAPI.UpdateWeaponSkin / UpdateKnifeSkin / UpdateGloveSkin
```

`defindex` 决定走哪个分支：`500~526` 刀、`≥4725` 手套、其余武器。

### 两个协议细节

1. **`paintwear` 是 uint32 存的 float 位模式**，要用 `BitConverter.Int32BitsToSingle` 还原。
2. **载荷尾部可能多出 4 个字节**（不是校验和）。它会被误读成「第二个 defindex」，
   所以解码器对每个标量字段**只认第一次出现**，读不下去就停。

## 命令

| 命令 | 说明 |
| --- | --- |
| `!buff <分享码/链接>` | 换成链接里的那件 |
| `!buffcookie <cookie>` | 更新 BUFF Cookie（整串粘贴，可带 `Cookie:` 前缀） |
| `!buffcookie clear` | 清除已保存的 Cookie |
| `sw_bbtest` | 控制台自检：版本 / WeaponSkins API / Cookie / 代理 |
| `sw_bbtest <分享码/链接/assetid>` | 只解析数据，不修改任何人的皮肤 |

### 参数写法

| 写法 | 说明 |
| --- | --- |
| `u86YQwb6` | 分享码，即 `market/s/` 后面那段 |
| `buff.163.com/market/s/u86YQwb6` | 省略 scheme |
| `https://buff.163.com/...` | 完整链接（**控制台里不行**，见下） |
| `54116611346` | 直接给 assetid |

> Source 引擎控制台把 `//` 之后的内容当注释截掉，所以 `sw_bbtest https://...` 传到插件里只剩
> `https:`。控制台和游戏聊天里都建议直接发**分享码**。

## 配置

插件目录下 `config.json`（`Load` 时读一次，改完 `sw plugins reload BuffBridge`）：

```json
{
  "UserAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ... Chrome/126.0.0.0 Safari/537.36",
  "Cookie": "session=...",
  "Proxy": "",
  "CooldownSeconds": 3,
  "RefreshAfterApply": true
}
```

| 字段 | 说明 |
| --- | --- |
| `Cookie` | 初始 Cookie；存在 `cookie.txt` 时以 `cookie.txt` 为准 |
| `Proxy` | 例如 `http://127.0.0.1:7890`；服务器出口 IP 被风控时使用 |
| `CooldownSeconds` | 每玩家冷却秒数 |
| `RefreshAfterApply` | `true` 时换完立刻重建手上武器；`false` 则需要重新购买才看到 |
| `UserAgent` | BUFF 对异常 UA 敏感，建议保持浏览器串 |

运行期 `!buffcookie` 写入的是 `cookie.txt`，重启后仍生效，不会改动 `config.json`。

### 取 Cookie

浏览器登录 BUFF → F12 → Network → 刷新 → 任一 `buff.163.com` 请求 →
Request Headers 的 `Cookie` → 整串复制 → 游戏里 `!buffcookie <粘贴>`。

## 构建

需要 **.NET 10 SDK**，另需两个运行时程序集放在 `lib/`：

| 文件 | 来源 |
| --- | --- |
| `WeaponSkins.Shared.dll` | WeaponSkins Release 附件里的 `WeaponSkins/`；或服务器的 `plugins/WeaponSkins/resources/exports/` |
| `SwiftlyS2.CS2.dll` | 服务器 SwiftlyS2 安装目录；或 NuGet 包 `SwiftlyS2.CS2` 的 `lib/net10.0/` |

> 两个 DLL 的版本必须和服务器一致，否则运行时会 `TypeLoadException` / `MissingMethodException`。
> `lib/*.dll` 已在 `.gitignore` 中，不入库。

```powershell
.\build.ps1          # 或双击 build.bat（会绕过执行策略）
```

产物在 `build/publish/BuffBridge/` 与 `build/BuffBridge.zip`。
`csproj` 里清空了 `RestoreSources`，依赖全部走 `lib/`，**编译过程不访问 NuGet**。

## 部署

```
addons/swiftlys2/plugins/BuffBridge/
  BuffBridge.dll
  BuffBridge.deps.json
  config.json
  cookie.txt        # 可选，!buffcookie 自动生成
```

重启服务器或控制台 `sw plugins reload BuffBridge`，然后：

```
sw plugins list
sw_bbtest
sw_bbtest u86YQwb6
```

## 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| `sw_bbtest` 未知命令 | 插件没加载；看日志里的 loader 报错，多半是 DLL 版本 |
| `WeaponSkins API：未注入` | WeaponSkins 没加载，或 `WeaponSkins.Shared.dll` 版本不符 |
| `解析失败：BUFF 返回 403` | 出口 IP 被风控，配 `Proxy` |
| `解析失败：BUFF 要求登录` | Cookie 失效，`sw_buffcookie session=...` |
| `解析失败：图片数据解码失败` | 载荷结构变化，提 issue 并附链接 |
| 印花位置不对 | 检查 `scale` / `offset` 是否被游戏版本改变 |

## 已知限制

- 只作用于**当前阵营**，换边需要重新发一次
- 没有请求队列与缓存，人多的服建议自行加限流
- 挂件字段按 `pattern` 当 seed 处理，缺少真实样本验证
- 印花 `sticker_id` 的命名空间、`Schema=1337` 的影响需要进游戏确认

## 安全

- `config.json` 与 `cookie.txt` **都是明文**，等同账号会话凭据
- 拿到这两个文件的人可以以该 BUFF 账号身份操作，建议使用**专用小号**
- 仓库里不应出现真实 Cookie：`.gitignore` 已排除，提交前请再确认
- 若 Cookie 曾经进入过公开仓库/聊天记录，请到 BUFF 退出登录使其失效

## 免责声明

- 仅用于自建服务器与朋友之间的娱乐
- 抓取 BUFF 数据可能违反其服务条款，使用风险自负
- 不得用于绕过反作弊、商业售卖或任何破坏公平性的场景

## 致谢

- [CS2-BuffInspector](https://github.com/ELDment/CS2-BuffInspector)：同类实现，本项目受其思路启发、独立编写
- [WeaponSkins](https://github.com/samyycX/WeaponSkins)：提供 `IWeaponSkinAPI`
- [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2)：插件框架

## License

[AGPL-3.0](LICENSE)。

本项目通过运行时接口依赖 [WeaponSkins](https://github.com/samyycX/WeaponSkins)（AGPL-3.0），
因此采用同一许可证。
