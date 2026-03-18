$ErrorActionPreference = 'Stop'

$discoveryRoot = 'C:\Discovery'
$inputDir = Join-Path $discoveryRoot 'Input'
$outputDir = Join-Path $discoveryRoot 'Output'
$scriptsDir = Join-Path $discoveryRoot 'Scripts'
$logsDir = Join-Path $discoveryRoot 'Logs'
$taskName = 'AppCatalogueDiscoveryWatcher'
$sourceDir = Split-Path -Path $PSCommandPath -Parent

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Install-DiscoveryBootstrap.ps1 must be run in an elevated PowerShell session.'
}

foreach ($path in @($inputDir, $outputDir, $scriptsDir, $logsDir)) {
    New-Item -Path $path -ItemType Directory -Force | Out-Null
}

foreach ($scriptName in @('Run-Discovery.ps1', 'Discovery-Watcher.ps1')) {
    $sourcePath = Join-Path $sourceDir $scriptName
    $destinationPath = Join-Path $scriptsDir $scriptName
    if (-not (Test-Path $sourcePath)) {
        throw "Required script missing: $sourcePath"
    }

    Copy-Item -Path $sourcePath -Destination $destinationPath -Force
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptsDir\Discovery-Watcher.ps1`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 0)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host ''
Write-Host 'Discovery bootstrap setup completed successfully.' -ForegroundColor Green
Write-Host "Task '$taskName' installed and started."
Write-Host ''
Write-Host 'Important next step:'
Write-Host '1. Confirm watcher is running (Task Scheduler > AppCatalogueDiscoveryWatcher).'
Write-Host '2. Create/update checkpoint named CleanState AFTER this setup.'
