# Снимает баннеры README из docs/banner.html: docs/banner.png (светлый) и docs/banner-dark.png (тёмный).
# Макет 1280×400, PNG в двойном разрешении (2560×800), чтобы текст был чётким на экранах с высокой плотностью.
# Нужны Microsoft Edge и интернет (шрифты Golos Text и JetBrains Mono берутся из Google Fonts).
# Запуск: powershell -File docs\make-banner.ps1
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Не найден Microsoft Edge" }
$page = ([Uri](Join-Path $here "banner.html")).AbsoluteUri
foreach ($v in @(@("light", "banner.png"), @("dark", "banner-dark.png"))) {
    $out = Join-Path $here $v[1]
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 `
        --window-size=1280,400 --virtual-time-budget=15000 "--screenshot=$out" "$page#$($v[0])" | Out-Null
    if (-not (Test-Path $out)) { throw "Не получился $($v[1])" }
    "docs/" + $v[1]
}
