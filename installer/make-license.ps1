# Окно лицензии в установщике: installer/License.rtf из LICENSE.md (единственный источник текста соглашения).
# Запускается из build.ps1 при каждой сборке. Тексты лицензий сторонних компонентов ставятся рядом с программой файлом.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Кириллица и типографские знаки — через \uN?, спецсимволы RTF экранируются
function Rtf([string]$s) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') { [void]$sb.Append('\').Append($ch) }
        elseif ($c -gt 127) { [void]$sb.Append('\u').Append($c).Append('?') }
        else { [void]$sb.Append($ch) }
    }
    $sb.ToString()
}
# **полужирный** → {\b …}; `код` — без обратных кавычек
function Inline([string]$s) {
    $parts = [regex]::Split($s, '\*\*')
    $out = ""
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $t = Rtf ($parts[$i] -replace '`', '')
        $out += if ($i % 2 -eq 1) { "{\b $t}" } else { $t }
    }
    $out
}

$lines = [IO.File]::ReadAllLines((Join-Path $root "LICENSE.md"), [Text.Encoding]::UTF8)
$body = New-Object Text.StringBuilder
foreach ($line in $lines) {
    $l = $line.TrimEnd()
    if ($l -like "<!--*") { continue }
    if ($l -match '^\|[-| ]+\|$') { continue }                      # разделитель таблицы
    if ($l -match '^# (.+)') { [void]$body.Append("{\b\fs24 " + (Inline $Matches[1]) + "}\par`n"); continue }
    if ($l -match '^## (.+)') { [void]$body.Append("{\b " + (Inline $Matches[1]) + "}\par`n"); continue }
    if ($l -match '^\|(.+)\|$') {                                   # строка таблицы → «ячейка — ячейка — ячейка»
        $cells = $Matches[1].Split('|') | ForEach-Object { $_.Trim() }
        [void]$body.Append((Inline ($cells -join ' — ')) + "\par`n"); continue
    }
    if ($l -eq "") { [void]$body.Append("\par`n"); continue }
    [void]$body.Append((Inline $l) + "\par`n")
}
$rtf = "{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\uc1\f0\fs18`n" + $body.ToString() + "}"
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "License.rtf"), $rtf, [Text.Encoding]::ASCII)
"installer/License.rtf"
