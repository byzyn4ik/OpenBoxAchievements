# End-to-end check: the Extra Options test build hands telemetry to
# fh2-achievements.dll, which talks to OpenBox's pipe listener. Works only in
# ipc-check\<guid>. Usage:
#   .\verify-ipc.ps1 [-Mod <mod test DLL>] [-Rules <fh2-achievements.dll>]
param(
    [string]$Mod = (Join-Path $PSScriptRoot '..\ForzaHorizon2_ExtraOptions-0.2.98-openbox\out\release-test\winmm-test.dll'),
    [string]$Rules = (Join-Path $PSScriptRoot '..\ForzaHorizon2_ExtraOptions-0.2.98-openbox\out\release\achievements\fh2-achievements.dll'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -property installationPath
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('mscorlib', 'System', 'System.Core', 'System.Xaml', 'System.Drawing', 'System.Windows.Forms',
          'System.Web.Extensions', 'System.Security', 'Microsoft.VisualBasic',
          'WPF\PresentationCore', 'WPF\PresentationFramework', 'WPF\WindowsBase') |
    ForEach-Object { '/reference:' + (Join-Path $fx "$_.dll") }
$work = Join-Path $PSScriptRoot ('ipc-check\' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$exe = Join-Path $work 'OpenBoxIpcChecks.exe'
$sources = @(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'tests\IpcChecks.cs'
& $csc /nologo /noconfig /nostdlib /codepage:65001 /langversion:latest /target:exe /platform:x64 `
    /main:OpenBoxAchievements.IpcChecks "/out:$exe" @refs @sources
if ($LASTEXITCODE) { throw 'IPC test compilation failed.' }
& $exe $work (Resolve-Path $Mod) (Resolve-Path $Rules)
if ($LASTEXITCODE) { throw 'IPC tests failed.' }
