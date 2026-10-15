# Рисует баннер для README: docs/banner.png (1280×360), в фирменных цветах приложения.
# Запуск: powershell -File docs\make-banner.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$iconPath = Join-Path $here "..\src\ShablonRabot.App\Assets\app.ico"

function Get-IconFrame([string]$path, [int]$size) {
    $b = [IO.File]::ReadAllBytes($path)
    $count = [BitConverter]::ToUInt16($b, 4)
    for ($i = 0; $i -lt $count; $i++) {
        $o = 6 + 16 * $i
        $w = if ($b[$o] -eq 0) { 256 } else { $b[$o] }
        if ($w -ne $size) { continue }
        $len = [BitConverter]::ToInt32($b, $o + 8); $off = [BitConverter]::ToInt32($b, $o + 12)
        return [Drawing.Image]::FromStream((New-Object IO.MemoryStream(, $b[$off..($off + $len - 1)])))
    }
    throw "В иконке нет кадра $size×$size"
}
function C([string]$hex, [int]$a = 255) { [Drawing.Color]::FromArgb($a, [Drawing.ColorTranslator]::FromHtml($hex)) }
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); return $p
}
# Лист документа: шапка, заголовок, строки текста
function Draw-Sheet($g, [float]$x, [float]$y, [float]$w, [float]$h, [int]$alpha, [float]$angle) {
    $state = $g.Save()
    $g.TranslateTransform($x + $w / 2, $y + $h / 2); $g.RotateTransform($angle); $g.TranslateTransform(-$w / 2, -$h / 2)
    $sheet = RoundRect 0 0 $w $h 10
    $g.FillPath((New-Object Drawing.SolidBrush (C "#FFFFFF" $alpha)), $sheet)
    $ink = New-Object Drawing.SolidBrush (C "#005FB8" ([int]($alpha * 0.55)))
    $g.FillRectangle($ink, $w * 0.3, 26, $w * 0.4, 8)
    for ($i = 0; $i -lt 9; $i++) { $g.FillRectangle((New-Object Drawing.SolidBrush (C "#5A6B7D" ([int]($alpha * 0.35)))), 22, 56 + $i * 18, $w - 44 - ($i % 3) * 18, 6) }
    $g.Restore($state)
}

$W = 1280; $H = 360
$bmp = New-Object Drawing.Bitmap($W, $H)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = "AntiAlias"; $g.InterpolationMode = "HighQualityBicubic"; $g.TextRenderingHint = "AntiAliasGridFit"
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(0, 0, $W, $H)), (C "#1A6FC0"), (C "#003E7A"), 20)
$g.FillRectangle($grad, 0, 0, $W, $H)
# Листы справа
Draw-Sheet $g 1040 64 200 290 60 8
Draw-Sheet $g 905 40 200 290 120 -4
Draw-Sheet $g 1050 128 200 290 200 3
# Иконка на плашке
$tile = RoundRect 80 92 120 120 26
$g.FillPath((New-Object Drawing.SolidBrush (C "#FFFFFF")), $tile)
$g.DrawImage((Get-IconFrame $iconPath 128), 96, 108, 88, 88)
# Название и подзаголовок
$white = New-Object Drawing.SolidBrush (C "#FFFFFF")
$soft = New-Object Drawing.SolidBrush (C "#FFFFFF" 220)
$g.DrawString("Чистовик", (New-Object Drawing.Font("Segoe UI Semibold", 64, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)), $white, 228, 78)
$g.DrawString("Шаблоны студенческих работ по ГОСТ для Word", (New-Object Drawing.Font("Segoe UI", 26, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)), $soft, 234, 168)
$g.DrawString("Курсовые · ВКР · рефераты · отчёты по практике · вузы и колледжи", (New-Object Drawing.Font("Segoe UI", 19, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)), (New-Object Drawing.SolidBrush (C "#FFFFFF" 170)), 236, 214)
$g.Dispose()
$bmp.Save((Join-Path $here "banner.png"), [Drawing.Imaging.ImageFormat]::Png)
"docs/banner.png"
