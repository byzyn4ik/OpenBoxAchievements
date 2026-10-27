# Installs (or with -Uninstall removes) the OpenBox Xbox Game Bar widget for
# the current user. Run by the OpenBox Achievements MSI from the widget folder:
#   OpenBoxWidget.msix, OpenBoxWidget.cer, Dependencies\*.appx
# The widget is signed with its own self-signed certificate; trusting it is
# the one step that needs an administrator (a UAC prompt).
param([string]$Folder = $PSScriptRoot, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'OpenBoxWidget-install.log'
function Note($text) { Add-Content -Path $log -Value ("{0:s} {1}" -f (Get-Date), $text) }

if ($Uninstall) {
    Get-AppxPackage OpenBoxAchievements.Widget | Remove-AppxPackage
    Note 'widget removed'
    exit 0
}

try {
    $cer = Join-Path $Folder 'OpenBoxWidget.cer'
    $thumb = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
        Note 'trusting the widget certificate (administrator prompt)'
        $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList `
            "-NoProfile -Command Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { throw 'the certificate was not trusted (prompt declined?)' }
    }
    foreach ($dependency in Get-ChildItem (Join-Path $Folder 'Dependencies') -Filter *.appx) {
        # Already installed (or newer, or in use) is fine.
        try { Add-AppxPackage -Path $dependency.FullName } catch { Note "dependency $($dependency.Name): $($_.Exception.Message)" }
    }
    Add-AppxPackage -Path (Join-Path $Folder 'OpenBoxWidget.msix') -ForceApplicationShutdown
    Note "widget installed: $((Get-AppxPackage OpenBoxAchievements.Widget).Version)"
} catch {
    Note "widget not installed: $($_.Exception.Message)"
    exit 1
}
