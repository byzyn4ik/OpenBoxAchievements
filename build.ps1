# Builds OpenBox Achievements (WPF, .NET Framework 4.8) with the Roslyn
# compiler of Visual Studio or its Build Tools, into a ready-to-copy folder:
#   <Output>\OpenBox Achievements.exe, games\, docs\, README.md
# Usage: .\build.ps1 [-Configuration Release|Debug] [-Output <folder>]
#   (default: Release into .\bin; Debug into .\bin-debug, with a .pdb)
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release', [string]$Output)
$ErrorActionPreference = 'Stop'
if (-not $Output) { $Output = Join-Path $PSScriptRoot $(if ($Configuration -eq 'Debug') { 'bin-debug' } else { 'bin' }) }
$options = @(if ($Configuration -eq 'Debug') { '/optimize-', '/debug:full', '/define:DEBUG;TRACE' } else { '/optimize+' })

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio or Visual Studio Build Tools is required (vswhere.exe not found).' }
$vs = & $vswhere -latest -products '*' -property installationPath
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { throw "Roslyn compiler not found: $csc" }

$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('mscorlib', 'System', 'System.Core', 'System.Xaml', 'System.Drawing', 'System.Windows.Forms',
          'System.Web.Extensions', 'System.Security', 'Microsoft.VisualBasic',
          'WPF\PresentationCore', 'WPF\PresentationFramework', 'WPF\WindowsBase') |
    ForEach-Object { '/reference:' + (Join-Path $fx "$_.dll") }

[void][IO.Directory]::CreateDirectory($Output)
$exe = Join-Path $Output 'OpenBox Achievements.exe'
$icon = Join-Path $PSScriptRoot 'assets\openbox.ico' # tools\Make-Icon.ps1
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName
& $csc /nologo /noconfig /nostdlib /codepage:65001 /langversion:latest /target:winexe /platform:anycpu `
    @options "/win32icon:$icon" "/out:$exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The shareable files beside the EXE.
Copy-Item (Join-Path $PSScriptRoot 'games') $Output -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'docs') $Output -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md') $Output -Force
Get-Item $exe | Select-Object FullName, Length
