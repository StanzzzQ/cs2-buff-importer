# lib/

把 `WeaponSkins.Shared.dll` 放到这个目录，编译时需要它来解析 `IWeaponSkinAPI`。

来源（二选一）：

1. 服务器上现有插件目录：
   `addons/swiftlys2/plugins/WeaponSkins/resources/exports/WeaponSkins.Shared.dll`
2. WeaponSkins 仓库：<https://github.com/samyycX/WeaponSkins> → `resources/exports/WeaponSkins.Shared.dll`

注意事项：

- 版本必须和服务器上运行的 WeaponSkins **完全一致**，否则运行时会因为 `IWeaponSkinAPI` 签名变化报
  `MissingMethodException` / `TypeLoadException`。
- 这个 DLL 只用于编译，`Private=false`，不会被打进发布目录。
- `lib/*.dll` 已在 `.gitignore` 里，别把二进制提交进仓库。
