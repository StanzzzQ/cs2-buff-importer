param(
    [string]$Url = "https://buff.163.com/market/s/xEcyB2W4",
    [string]$Cookie = "",
    [string]$Proxy = "",
    [string]$Out = "buff-page.html"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"

$headers = @{
    "User-Agent"      = $ua
    "Accept"          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
    "Accept-Language" = "zh-CN,zh;q=0.9"
}

if ($Cookie) {
    $headers["Cookie"] = $Cookie
}

$params = @{
    Uri                = $Url
    Headers            = $headers
    TimeoutSec         = 60
    SkipHttpErrorCheck = $true
}

if ($Proxy) {
    $params["Proxy"] = $Proxy
}

Write-Host "GET $Url" -ForegroundColor Cyan
if ($Cookie) { Write-Host "  (带 Cookie，长度 $($Cookie.Length))" }

try {
    $resp = Invoke-WebRequest @params
}
catch {
    Write-Host "请求直接失败: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$finalUrl = ""
try { $finalUrl = $resp.BaseResponse.RequestMessage.RequestUri.AbsoluteUri } catch { }

Write-Host "最终地址 : $finalUrl"
Write-Host "状态码   : $([int]$resp.StatusCode)" -ForegroundColor $(if ([int]$resp.StatusCode -eq 200) { "Green" } else { "Red" })
Write-Host "正文长度 : $($resp.Content.Length)"
Write-Host "Content-Type: $($resp.Headers['Content-Type'])"

$html = $resp.Content
$markerCount = ([regex]::Matches($html, "var items")).Count

Write-Host "var items 出现次数: $markerCount" -ForegroundColor $(if ($markerCount -gt 0) { "Green" } else { "Yellow" })
Write-Host "含 assetid        : $($html -match 'assetid')"
Write-Host "含 paintwear      : $($html -match 'paintwear')"
Write-Host "像是登录页        : $($html -match 'Scan to log in|扫码登录')"

if ($html) {
    $html | Set-Content -Path $Out -Encoding utf8
    Write-Host "已保存: $((Resolve-Path $Out).Path)" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "判断方法：" -ForegroundColor Cyan
Write-Host "  1. 在你自己的电脑上跑一次（家里宽带）"
Write-Host "  2. 在游戏服务器上跑一次"
Write-Host "  如果电脑 200、服务器 403 → 是服务器 IP 被风控，用 -Proxy 指一个国内代理"
Write-Host "  如果两边都 403    → 多半缺登录 Cookie，用 -Cookie 'session=...;' 再试"
