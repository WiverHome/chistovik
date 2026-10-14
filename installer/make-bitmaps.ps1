# Рисует фирменные картинки установщика (WixUI): Dialog.bmp 493×312 и Banner.bmp 493×58.
# Цвета — из темы приложения (Color.Accent.*), иконка — Assets/app.ico (кадр 128×128).
# Запуск: powershell -File installer\make-bitmaps.ps1 (результат — рядом со скриптом; build.ps1 берёт готовые файлы).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$iconPath = Join-Path $here "..\src\ShablonRabot.App\Assets\app.ico"

function Get-IconFrame([string]$path, [int]$size) {
    # Кадры иконки хранятся как PNG — берём нужный по заголовку ICO
    $b = [IO.File]::ReadAllBytes($path)
    $count = [BitConverter]::ToUInt16($b, 4)
    for ($i = 0; $i -lt $count; $i++) {
        $o = 6 + 16 * $i
        $w = if ($b[$o] -eq 0) { 256 } else { $b[$o] }
        if ($w -ne $size) { continue }
        $len = [BitConverter]::ToInt32($b, $o + 8); $off = [BitConverter]::ToInt32($b, $o + 12)
        $ms = New-Object IO.MemoryStream(, $b[$off..($off + $len - 1)])
        return [Drawing.Image]::FromStream($ms)
    }
    throw "В иконке нет кадра $size×$size"
}

function C([string]$hex, [int]$a = 255) { $c = [Drawing.ColorTranslator]::FromHtml($hex); [Drawing.Color]::FromArgb($a, $c) }

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.InterpolationMode = "HighQualityBicubic"; $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([Drawing.Color]::White)
    return $bmp, $g
}

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); return $p
}

# Стилизованный лист документа: контур, шапка и строки текста
function Draw-Sheet($g, [float]$x, [float]$y, [float]$w, [float]$h, [int]$alpha) {
    $sheet = RoundRect $x $y $w $h 6
    $g.FillPath((New-Object Drawing.SolidBrush (C "#FFFFFF" ([int]($alpha * 0.55)))), $sheet)
    $g.DrawPath((New-Object Drawing.Pen((C "#FFFFFF" $alpha), 1.5)), $sheet)
    $line = New-Object Drawing.SolidBrush (C "#FFFFFF" $alpha)
    $g.FillRectangle($line, $x + 12, $y + 14, $w * 0.45, 4)
    for ($i = 0; $i -lt 6; $i++) { $g.FillRectangle($line, $x + 12, $y + 30 + $i * 11, $w - 24 - ($i % 3) * 10, 3) }
}

$accent = C "#005FB8"; $accentDark = C "#003E7A"; $accentLight = C "#1A6FC0"
$icon = Get-IconFrame $iconPath 128

# ---------------------------------------------------------------- Dialog.bmp (приветствие, завершение)
$panel = 164
$bmp, $g = New-Canvas 493 312
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(0, 0, $panel, 312)), $accentLight, $accentDark, 70)
$g.FillRectangle($grad, 0, 0, $panel, 312)
# Листы-«шаблоны» внизу панели
$g.SetClip((New-Object Drawing.Rectangle(0, 0, $panel, 312)))
Draw-Sheet $g 74 226 88 110 46
Draw-Sheet $g 44 244 88 110 70
$g.ResetClip()
# Иконка на белой плашке
$tile = RoundRect 22 24 64 64 12
$g.FillPath((New-Object Drawing.SolidBrush (C "#FFFFFF")), $tile)
$g.DrawImage($icon, 30, 32, 48, 48)
# Название и подпись
$title = New-Object Drawing.Font("Segoe UI Semibold", 15, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$small = New-Object Drawing.Font("Segoe UI", 12, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$white = New-Object Drawing.SolidBrush (C "#FFFFFF")
$soft = New-Object Drawing.SolidBrush (C "#FFFFFF" 215)
$g.DrawString("Конструктор`nшаблонов работ", $title, $white, (New-Object Drawing.RectangleF(20, 102, ($panel - 22), 44)))
$g.DrawString("Курсовые, ВКР, рефераты и отчёты — сразу по ГОСТ", $small, $soft, (New-Object Drawing.RectangleF(21, 150, ($panel - 30), 62)))
# Тонкая граница панели
$g.FillRectangle((New-Object Drawing.SolidBrush (C "#E6F0FA")), $panel, 0, 3, 312)
$g.Dispose()
$bmp.Save((Join-Path $here "Dialog.bmp"), [Drawing.Imaging.ImageFormat]::Bmp)

# ---------------------------------------------------------------- Banner.bmp (лицензия, ход установки)
$bmp, $g = New-Canvas 493 58
# Справа — фирменный скос; слева остаётся белое поле под заголовок и описание WixUI
$shape = New-Object Drawing.Drawing2D.GraphicsPath
$shape.AddPolygon([Drawing.PointF[]]@((New-Object Drawing.PointF(418, 0)), (New-Object Drawing.PointF(493, 0)), (New-Object Drawing.PointF(493, 58)), (New-Object Drawing.PointF(398, 58))))
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(398, 0, 95, 58)), $accentLight, $accentDark, 45)
$g.FillPath($grad, $shape)
$tile = RoundRect 436 9 40 40 9
$g.FillPath((New-Object Drawing.SolidBrush (C "#FFFFFF")), $tile)
$g.DrawImage($icon, 442, 15, 28, 28)
$g.Dispose()
$bmp.Save((Join-Path $here "Banner.bmp"), [Drawing.Imaging.ImageFormat]::Bmp)

Get-ChildItem $here -Filter *.bmp | Select-Object Name, Length
