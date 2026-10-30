# Automated tests of OpenBox Achievements: builds the app, then runs it
# headless in a sandbox (its own data, widget folder, pipe and instance names;
# a running OpenBox and your data are not touched) and checks, through what
# the Game Bar widget would show, which game is being played: a game's mod on
# the pipe (the bus), its heartbeats, timeout and process exit; the process
# name only for games whose manifest names one; time played only while active.
# With the FH2 plugin built (its repo beside this one, or -Plugin <its out
# folder>), the real plugin DLL is also run against the app.
# Usage: .\run-tests.ps1 [-Plugin <FH2 plugin out folder>] [-NoPlugin]
param([string]$Plugin = (Join-Path $PSScriptRoot '..\FH2-ExtraOptions-OpenBox-Achievements\out'), [switch]$NoPlugin)
$ErrorActionPreference = 'Stop'
$work = Join-Path $PSScriptRoot ('out\tests\bus-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug -Output (Join-Path $work 'app') | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -property installationPath
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('mscorlib', 'System', 'System.Core', 'System.Web.Extensions') | ForEach-Object { '/reference:' + (Join-Path $fx "$_.dll") }
$checks = Join-Path $work 'BusChecks.exe'
& $csc /nologo /noconfig /nostdlib /codepage:65001 /langversion:latest /target:exe /platform:anycpu `
    "/out:$checks" @refs (Join-Path $PSScriptRoot 'tests\BusChecks.cs')
if ($LASTEXITCODE) { throw 'Test compilation failed.' }

$arguments = @($work)
$hostExe = Join-Path $Plugin 'tests\plugin_test.exe'
$dll = Join-Path $Plugin 'mods\OpenBox Achievements\plugins\OpenBoxAchievements.dll'
if (-not $NoPlugin -and (Test-Path $hostExe) -and (Test-Path $dll)) { $arguments += $hostExe, $dll }
& $checks @arguments
$failed = $LASTEXITCODE
if (-not $failed) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
if ($failed) { throw "Bus checks failed (work folder kept: $work)" }
