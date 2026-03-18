$ErrorActionPreference = 'Stop'

$hostRoot = 'C:\Installers'
$discoveryRoot = Join-Path $hostRoot 'Discovery'
$hostStaging = Join-Path $discoveryRoot 'HostStaging'
$results = Join-Path $discoveryRoot 'Results'
$logs = Join-Path $hostRoot 'Logs'
$scriptTarget = Join-Path $discoveryRoot 'Scripts'
$guestTarget = Join-Path $scriptTarget 'Guest'
$repoRoot = Split-Path -Path $PSCommandPath -Parent
$guestSource = Join-Path $repoRoot 'AppCatalogueAdmin\DiscoveryScripts\Guest'

foreach ($path in @($hostRoot, $discoveryRoot, $hostStaging, $results, $logs, $scriptTarget, $guestTarget)) {
    New-Item -Path $path -ItemType Directory -Force | Out-Null
}

if (-not (Test-Path $guestSource)) {
    throw "Guest script source folder not found: $guestSource"
}

Copy-Item -Path (Join-Path $guestSource '*') -Destination $guestTarget -Force

Write-Host ''
Write-Host 'Discovery host preparation completed.' -ForegroundColor Green
Write-Host "Guest script package copied to: $guestTarget"
Write-Host "Log folder ensured: $logs"
Write-Host ''
Write-Host 'Next: run Prepare-DiscoveryVmGuest.ps1 to copy scripts into VM.'
