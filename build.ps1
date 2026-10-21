# Сборка «Чистовика»: самодостаточный exe (.NET не нужен) + MSI-установщик.
# Требуется: .NET 8 SDK и WiX как dotnet-инструмент:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.UI.wixext/5.0.2
param([string]$Version = "1.4.2")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
$dist = Join-Path $root "dist"

Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish "$root\src\ShablonRabot.App\ShablonRabot.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "Ошибка публикации" }

Copy-Item "$publish\Chistovik.exe" "$dist\Chistovik.exe" -Force

# Картинки мастера установки (если их нет — нарисовать)
if (-not (Test-Path "$root\installer\Dialog.bmp") -or -not (Test-Path "$root\installer\Banner.bmp")) {
    & "$root\installer\make-bitmaps.ps1" | Out-Null
}

# Окно лицензии в установщике — из LICENSE.md
& "$root\installer\make-license.ps1" | Out-Null

wix build "$root\installer\Package.wxs" -ext WixToolset.UI.wixext -culture ru-RU -loc "$root\installer\Package.ru-RU.wxl" -arch x64 `
    -d "AppDir=$publish" -d "Version=$Version" -d "IconPath=$root\src\ShablonRabot.App\Assets\app.ico" `
    -o "$dist\Chistovik-$Version.msi"
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки установщика" }

Remove-Item "$dist\*.wixpdb" -ErrorAction SilentlyContinue
Get-ChildItem $dist | Select-Object Name, @{n = "МБ"; e = { [math]::Round($_.Length / 1MB, 1) } }
