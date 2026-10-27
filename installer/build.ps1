# Builds the OpenBox Achievements setup, <Output>\OpenBoxAchievements-<version>.msi:
# the app (built fresh with ..\build.ps1, or -App <built folder>) and, with
# -Widget, the Xbox Game Bar widget (a folder with OpenBoxWidget.msix,
# OpenBoxWidget.cer and Dependencies\*.appx). Needs WiX 3.14 binaries (-Wix).
# Usage: .\build.ps1 -Wix <wix314 folder> [-Version 0.0.1] [-App <folder>] [-Widget <folder>] [-Output <folder>]
param([Parameter(Mandatory)][string]$Wix, [string]$Version = '0.0.0', [string]$App, [string]$Widget,
      [string]$Output = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$work = Join-Path $PSScriptRoot ('obj\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $work 'app'
New-Item -ItemType Directory -Force $appDir, $Output | Out-Null

# The app.
if ($App) { Copy-Item (Join-Path $App '*') $appDir -Recurse -Force }
else { & (Join-Path $root 'build.ps1') -Output $appDir | Out-Null }
Get-ChildItem $appDir -Recurse -File | Where-Object { $_.Name -like '*.bak*' -or $_.Extension -eq '.pdb' } | Remove-Item

# The widget, when given.
$widgetDir = $null
if ($Widget) {
    $widgetDir = Join-Path $work 'widget'
    New-Item -ItemType Directory -Force (Join-Path $widgetDir 'Dependencies') | Out-Null
    Copy-Item (Join-Path $Widget 'OpenBoxWidget.msix'), (Join-Path $Widget 'OpenBoxWidget.cer') $widgetDir
    Copy-Item (Join-Path $Widget 'Dependencies\*.appx') (Join-Path $widgetDir 'Dependencies')
    Copy-Item (Join-Path $PSScriptRoot 'install-widget.ps1') $widgetDir
}

# Dialog art in the app's colours.
Add-Type -AssemblyName System.Drawing
function Art($w, $h, $path, $text) {
    $b = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::White)
    $green = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 0x10, 0x7c, 0x10))
    if ($h -gt 100) { $g.FillRectangle($green, 0, 0, 164, $h) } else { $g.FillEllipse($green, $w - 52, 6, 44, 44) }
    if ($text) { $font = New-Object Drawing.Font 'Segoe UI Semibold', 13; $g.DrawString($text, $font, [Drawing.Brushes]::White, 14, 24) }
    $g.Dispose(); $b.Save($path, [Drawing.Imaging.ImageFormat]::Bmp)
}
Art 493 58 (Join-Path $work 'banner.bmp') $null
Art 493 312 (Join-Path $work 'dialog.bmp') "OpenBox`nAchievements"

$heat = Join-Path $Wix 'heat.exe'; $candle = Join-Path $Wix 'candle.exe'; $light = Join-Path $Wix 'light.exe'
$sources = @((Join-Path $PSScriptRoot 'OpenBox.wxs'), (Join-Path $work 'AppFiles.wxs'))
& $heat dir $appDir -nologo -gg -sfrag -srd -sreg -ke -dr INSTALLFOLDER -cg AppFiles -var var.AppDir -out $sources[1]
if ($LASTEXITCODE) { throw 'heat (app) failed' }
$defines = @("-dVersion=$Version", "-dAppDir=$appDir",
             "-dIcon=$(Join-Path $root 'assets\openbox.ico')",
             "-dBanner=$(Join-Path $work 'banner.bmp')", "-dDialog=$(Join-Path $work 'dialog.bmp')")
if ($widgetDir) {
    $sources += Join-Path $work 'WidgetFiles.wxs'
    & $heat dir $widgetDir -nologo -gg -sfrag -srd -sreg -ke -dr WIDGETFOLDER -cg WidgetFiles -var var.WidgetDir -out $sources[2]
    if ($LASTEXITCODE) { throw 'heat (widget) failed' }
    $defines += "-dWidgetDir=$widgetDir"
}
& $candle -nologo -arch x64 -ext WixUIExtension -ext WixUtilExtension @defines -out "$work\" @sources
if ($LASTEXITCODE) { throw 'candle failed' }
$objects = $sources | ForEach-Object { Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($_) + '.wixobj') }
$msi = Join-Path $Output "OpenBoxAchievements-$Version.msi"
# Per-user files keep file key paths: the ICEs that want registry key paths are skipped.
& $light -nologo -ext WixUIExtension -ext WixUtilExtension -cultures:en-us -sice:ICE38 -sice:ICE64 -sice:ICE91 -sice:ICE43 -sice:ICE57 `
    -out $msi @objects
if ($LASTEXITCODE) { throw 'light failed' }
Remove-Item $work -Recurse -Force
Get-Item $msi | Select-Object FullName, Length
