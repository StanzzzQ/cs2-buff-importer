# 本地一键编译 BuffBridge，并打包成可直接上传的 zip。
#
#   .\build.ps1
#   .\build.ps1 -SkipRestore
#   .\build.ps1 -Dotnet "C:\Program Files\dotnet\dotnet.exe"
#
# 优先用项目内 .dotnet\dotnet.exe（不污染系统），其次用 PATH 里的 dotnet。

param(
    [string]$Dotnet = "",
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Resolve-Dotnet {
    param([string]$Preferred)

    if ($Preferred) { return $Preferred }

    $local = Join-Path $root ".dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $local) { return $local }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw "找不到 dotnet：先装 .NET 10 SDK，或把 SDK 解压到 $root\.dotnet\"
}

$dotnet = Resolve-Dotnet $Dotnet

Write-Host "dotnet : $dotnet" -ForegroundColor Cyan
& $dotnet --version
if ($LASTEXITCODE -ne 0) { throw "dotnet 无法运行" }

$proj = Join-Path $root "BuffBridge.csproj"
$libSkins = Join-Path $root "lib\WeaponSkins.Shared.dll"
$libSwiftly = Join-Path $root "lib\SwiftlyS2.CS2.dll"

$missing = @()
if (-not (Test-Path -LiteralPath $libSkins)) { $missing += $libSkins }
if (-not (Test-Path -LiteralPath $libSwiftly)) { $missing += $libSwiftly }

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "缺少依赖 DLL：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "从服务器拿这两个文件（版本要和服务器一致）：" -ForegroundColor Yellow
    Write-Host "  WeaponSkins.Shared.dll  -> addons/swiftlys2/plugins/WeaponSkins/resources/exports/"
    Write-Host "  SwiftlyS2.CS2.dll       -> SwiftlyS2 安装目录（或从 NuGet 包里取 lib/net10.0/）"
    throw "缺少依赖 DLL"
}

if (-not $SkipRestore) {
    Write-Host ""
    Write-Host "== restore ==" -ForegroundColor Cyan
    & $dotnet restore $proj
    if ($LASTEXITCODE -ne 0) { throw "restore 失败" }
}

Write-Host ""
Write-Host "== publish ==" -ForegroundColor Cyan

# 先清掉整个 build 目录，避免上一次的 pdb 之类残留在产物里
$buildDir = Join-Path $root "build"
if (Test-Path -LiteralPath $buildDir) {
    Remove-Item -LiteralPath $buildDir -Recurse -Force
}

& $dotnet publish $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

$publishDir = Join-Path $root "build\publish\BuffBridge"
if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "没有找到输出目录：$publishDir"
}

# 发布产物里的 config.json 一律用模板覆盖：避免把开发机上的真 Cookie 打进 zip
$configPath = Join-Path $publishDir "config.json"
$example = Join-Path $root "config.example.json"
if (Test-Path -LiteralPath $example) {
    Copy-Item -LiteralPath $example -Destination $configPath -Force
    Write-Host "发布目录的 config.json 已重置为模板（Cookie 为空）" -ForegroundColor Yellow
}

# 注意：如果本地 config.json 里有 Cookie，它会被打进这个 zip，别公开分发
$zip = Join-Path $root "build\BuffBridge.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip

Write-Host ""
Write-Host "== 产物 ==" -ForegroundColor Green
Get-ChildItem -LiteralPath $publishDir | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "zip: $zip" -ForegroundColor Green
Write-Host ""
Write-Host "部署："
Write-Host "  1. 把 build\publish\BuffBridge\ 整个目录传到服务器"
Write-Host "     addons/swiftlys2/plugins/BuffBridge/"
Write-Host "  2. 重启服务器，或控制台执行：sw plugins reload BuffBridge"
