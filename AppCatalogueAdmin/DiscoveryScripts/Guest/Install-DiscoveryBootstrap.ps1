$ErrorActionPreference = 'Stop'

$discoveryRoot = 'C:\Discovery'
$inputDir = Join-Path $discoveryRoot 'Input'
$outputDir = Join-Path $discoveryRoot 'Output'
$scriptsDir = Join-Path $discoveryRoot 'Scripts'
$logsDir = Join-Path $discoveryRoot 'Logs'
$taskName = 'AppCatalogueDiscoveryWatcher'

function Resolve-ScriptSourceDirectory {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $candidates.Add($PSScriptRoot)
    }

    if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
        $commandParent = Split-Path -Path $PSCommandPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($commandParent)) {
            $candidates.Add($commandParent)
        }
    }

    $candidates.Add('C:\Discovery\Scripts')

    try {
        $cwd = (Get-Location).Path
        if (-not [string]::IsNullOrWhiteSpace($cwd)) {
            $candidates.Add($cwd)
        }
    }
    catch {
        # Best effort only.
    }

    $requiredScripts = @('Run-Discovery.ps1', 'Discovery-Watcher.ps1', 'Discovery-Logging.ps1')
    foreach ($candidate in ($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $allPresent = $true
        foreach ($scriptName in $requiredScripts) {
            if (-not (Test-Path (Join-Path $candidate $scriptName))) {
                $allPresent = $false
                break
            }
        }

        if ($allPresent) {
            return $candidate
        }
    }

    throw "Could not locate guest discovery scripts. Checked: $($candidates -join ', ')"
}

$sourceDir = Resolve-ScriptSourceDirectory

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Install-DiscoveryBootstrap.ps1 must be run in an elevated PowerShell session.'
}

foreach ($path in @($inputDir, $outputDir, $scriptsDir, $logsDir)) {
    New-Item -Path $path -ItemType Directory -Force | Out-Null
}

foreach ($scriptName in @('Run-Discovery.ps1', 'Discovery-Watcher.ps1', 'Discovery-Logging.ps1')) {
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
