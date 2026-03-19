param(
    [string]$VmName = 'AppCatalogueLab01',
    [string]$GuestScriptsPath = 'C:\Discovery\Scripts',
    [string]$HostGuestScriptSource = 'C:\Installers\Discovery\Scripts\Guest'
)

$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

if (-not (Test-Path $HostGuestScriptSource)) {
    throw "Host guest script source not found: $HostGuestScriptSource"
}

$requiredScripts = @(
    'Run-Discovery.ps1',
    'Discovery-Watcher.ps1',
    'Discovery-Logging.ps1',
    'Install-DiscoveryBootstrap.ps1'
)

foreach ($scriptName in $requiredScripts) {
    $source = Join-Path $HostGuestScriptSource $scriptName
    if (-not (Test-Path $source)) {
        throw "Required script missing: $source"
    }
}

$vm = Get-VM -Name $VmName -ErrorAction Stop
if ($vm.State -ne 'Running') {
    Start-VM -Name $VmName -ErrorAction Stop | Out-Null
}

Enable-VMIntegrationService -VMName $VmName -Name 'Guest Service Interface' -ErrorAction SilentlyContinue | Out-Null

foreach ($scriptName in $requiredScripts) {
    $source = Join-Path $HostGuestScriptSource $scriptName
    $destination = Join-Path $GuestScriptsPath $scriptName
    Copy-VMFile -Name $VmName -SourcePath $source -DestinationPath $destination -FileSource Host -CreateFullPath -Force -ErrorAction Stop
}

Write-Host ''
Write-Host 'Scripts copied to guest successfully.' -ForegroundColor Green
Write-Host ''
Write-Host 'Manual one-time in-guest action required (inside VM console):'
Write-Host '1. Sign in as DiscoveryAdmin'
Write-Host '2. Open elevated PowerShell'
Write-Host '3. Run: C:\Discovery\Scripts\Install-DiscoveryBootstrap.ps1'
Write-Host '4. After success, create/update checkpoint named CleanState'
