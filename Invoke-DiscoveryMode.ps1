param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$AppName = '',
    [string]$VmName = 'AppCatalogueLab01',
    [string]$CheckpointName = 'CleanState',
    [string]$HostStagingRoot = 'C:\Installers\Discovery\HostStaging',
    [string]$HostResultsRoot = 'C:\Installers\Discovery\Results',
    [string]$HostGuestScriptSource = 'C:\Installers\Discovery\Scripts\Guest',
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

if ([string]::IsNullOrWhiteSpace($AppName)) {
    $AppName = [System.IO.Path]::GetFileNameWithoutExtension($InstallerPath)
}

$jobId = "{0}_{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), ([guid]::NewGuid().ToString('N'))
$stagingDir = Join-Path $HostStagingRoot $jobId
$resultsDir = Join-Path $HostResultsRoot $jobId
New-Item -Path $stagingDir -ItemType Directory -Force | Out-Null
New-Item -Path $resultsDir -ItemType Directory -Force | Out-Null

$extension = [System.IO.Path]::GetExtension($InstallerPath)
if ([string]::IsNullOrWhiteSpace($extension)) { $extension = '.exe' }
$stagedInstaller = Join-Path $stagingDir ("installer{0}" -f $extension)
Copy-Item -Path $InstallerPath -Destination $stagedInstaller -Force

$job = [ordered]@{
    JobId = $jobId
    AppName = $AppName
    InstallerFileName = [System.IO.Path]::GetFileName($stagedInstaller)
    PreferredSilentArguments = ''
    ProbeTimeoutSeconds = 15
    InstallerTimeoutSeconds = 1200
    ShutdownVmOnComplete = $true
    SubmittedUtc = (Get-Date).ToUniversalTime().ToString('o')
}

$jobPath = Join-Path $stagingDir 'job.json'
$triggerPath = Join-Path $stagingDir 'run.trigger'
$job | ConvertTo-Json -Depth 6 | Set-Content -Path $jobPath -Encoding UTF8
Set-Content -Path $triggerPath -Value (Get-Date).ToUniversalTime().ToString('o') -Encoding UTF8

Write-Host 'Restoring checkpoint...'
Restore-VMCheckpoint -VMName $VmName -Name $CheckpointName -Confirm:$false -ErrorAction Stop

Write-Host 'Starting VM...'
$vm = Get-VM -Name $VmName -ErrorAction Stop
if ($vm.State -ne 'Running') {
    Start-VM -Name $VmName -ErrorAction Stop | Out-Null
}
Enable-VMIntegrationService -VMName $VmName -Name 'Guest Service Interface' -ErrorAction SilentlyContinue | Out-Null

Write-Host 'Copying guest scripts and job files...'
foreach ($scriptName in @('Run-Discovery.ps1', 'Discovery-Watcher.ps1', 'Discovery-Logging.ps1', 'Install-DiscoveryBootstrap.ps1')) {
    $sourceScript = Join-Path $HostGuestScriptSource $scriptName
    if (-not (Test-Path $sourceScript)) {
        throw "Required guest script missing: $sourceScript"
    }

    Copy-VMFile -Name $VmName -SourcePath $sourceScript -DestinationPath ("C:\Discovery\Scripts\{0}" -f $scriptName) -FileSource Host -CreateFullPath -Force -ErrorAction Stop
}

Copy-VMFile -Name $VmName -SourcePath $stagedInstaller -DestinationPath ("C:\Discovery\Input\{0}" -f [System.IO.Path]::GetFileName($stagedInstaller)) -FileSource Host -CreateFullPath -Force -ErrorAction Stop
Copy-VMFile -Name $VmName -SourcePath $jobPath -DestinationPath 'C:\Discovery\Input\job.json' -FileSource Host -CreateFullPath -Force -ErrorAction Stop
Copy-VMFile -Name $VmName -SourcePath $triggerPath -DestinationPath 'C:\Discovery\Input\run.trigger' -FileSource Host -CreateFullPath -Force -ErrorAction Stop

Write-Host 'Waiting for guest to power off after discovery...'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $state = (Get-VM -Name $VmName -ErrorAction Stop).State
    if ($state -eq 'Off') {
        break
    }

    Start-Sleep -Seconds 10
}

if ((Get-VM -Name $VmName -ErrorAction Stop).State -ne 'Off') {
    throw "Timed out waiting for discovery completion. Verify watcher bootstrap in guest."
}

Write-Host 'Collecting result files from VM disk...'
$diskPath = (Get-VMHardDiskDrive -VMName $VmName | Select-Object -First 1).Path
if ([string]::IsNullOrWhiteSpace($diskPath)) {
    throw "Could not resolve VM disk path for $VmName"
}

$mount = Mount-VHD -Path $diskPath -ReadOnly -PassThru -ErrorAction Stop
try {
    $disk = $mount | Get-Disk
    $volume = Get-Partition -DiskNumber $disk.Number | Get-Volume | Sort-Object -Property Size -Descending | Select-Object -First 1
    if ($null -eq $volume) {
        throw 'Could not locate mounted VM volume'
    }

    $source = Join-Path $volume.Path 'Discovery\Output'
    if (-not (Test-Path $source)) {
        throw "Guest output path not found: $source"
    }

    Copy-Item -Path (Join-Path $source '*') -Destination $resultsDir -Recurse -Force
}
finally {
    Dismount-VHD -Path $diskPath -ErrorAction SilentlyContinue
}

Write-Host 'Reverting VM to clean checkpoint...'
Restore-VMCheckpoint -VMName $VmName -Name $CheckpointName -Confirm:$false -ErrorAction Stop

$resultPath = Join-Path $resultsDir 'discovery-results.json'
Write-Host ''
Write-Host "Discovery run complete. Results: $resultPath" -ForegroundColor Green
