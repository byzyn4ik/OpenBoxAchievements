# Builds the Xbox Game Bar widget (UWP, Visual Studio with the Universal
# Windows Platform workload) and lays out what the setup takes (-Widget):
#   <Output>\OpenBoxWidget.msix, OpenBoxWidget.cer, Dependencies\*.appx (x64)
# The package is signed with the widget's certificate; its private key is not
# in the repository: pass the .pfx and its password.
# Usage: .\build.ps1 -KeyFile <OpenBoxWidget.pfx> -Password <password> [-Output <folder>]
param([Parameter(Mandatory)][string]$KeyFile, [Parameter(Mandatory)][string]$Password,
      [string]$Output = (Join-Path $PSScriptRoot 'package'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (Visual Studio with the UWP workload is required).' }

$packages = Join-Path $PSScriptRoot 'out'
if (Test-Path $packages) { Remove-Item $packages -Recurse -Force }
& $msbuild (Join-Path $PSScriptRoot 'OpenBoxWidget.csproj') /restore /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 `
    "/p:PackageCertificateKeyFile=$((Resolve-Path $KeyFile).Path)" "/p:PackageCertificatePassword=$Password"
if ($LASTEXITCODE) { throw 'Widget build failed.' }

$package = Get-ChildItem $packages -Directory | Select-Object -First 1
New-Item -ItemType Directory -Force (Join-Path $Output 'Dependencies') | Out-Null
Copy-Item (Get-ChildItem $package.FullName -Filter *.msix).FullName (Join-Path $Output 'OpenBoxWidget.msix') -Force
Copy-Item (Join-Path $PSScriptRoot 'OpenBoxWidget.cer') $Output -Force
Copy-Item (Join-Path $package.FullName 'Dependencies\x64\*.appx') (Join-Path $Output 'Dependencies') -Force
Get-ChildItem $Output -Recurse -File | Select-Object FullName, Length
