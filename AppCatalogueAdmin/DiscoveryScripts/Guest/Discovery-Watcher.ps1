param(
    [int]$PollSeconds = 4
)

$ErrorActionPreference = 'Stop'

$discoveryRoot = 'C:\Discovery'
$inputDir = Join-Path $discoveryRoot 'Input'
$outputDir = Join-Path $discoveryRoot 'Output'
$scriptsDir = Join-Path $discoveryRoot 'Scripts'
$logsDir = Join-Path $discoveryRoot 'Logs'

$jobPath = Join-Path $inputDir 'job.json'
$triggerPath = Join-Path $inputDir 'run.trigger'
$lockPath = Join-Path $inputDir 'processing.lock'
$watcherLogPath = Join-Path $logsDir 'Discovery-Watcher.log'
$runnerPath = Join-Path $scriptsDir 'Run-Discovery.ps1'
$hostSignalRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters'

New-Item -Path $inputDir -ItemType Directory -Force | Out-Null
New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
New-Item -Path $scriptsDir -ItemType Directory -Force | Out-Null
New-Item -Path $logsDir -ItemType Directory -Force | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "{0:u} {1}" -f (Get-Date), $Message
    Add-Content -Path $watcherLogPath -Value $line -Encoding UTF8
}

function Publish-HostSignal {
    param(
        [string]$Stage,
        [string]$Message,
        [string]$JobId = '',
        [bool]$ResultReady = $false
    )

    try {
        New-Item -Path $hostSignalRegistryPath -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryStage' -Value $Stage -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryMessage' -Value $Message -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryResultReady' -Value ($(if ($ResultReady) { 'true' } else { 'false' })) -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryJobId' -Value $JobId -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryUpdatedUtc' -Value ((Get-Date).ToUniversalTime().ToString('o')) -Type String -Force
    }
    catch {
        Write-Log "Watcher host signal update failed: $($_.Exception.Message)"
    }
}

Write-Log "Watcher started. PollSeconds=$PollSeconds"
Publish-HostSignal -Stage 'WatcherReady' -Message 'Watcher started and waiting for job.'

while ($true) {
    try {
        $hasJob = Test-Path $jobPath
        $hasTrigger = Test-Path $triggerPath

        if (($hasJob -or $hasTrigger) -and -not (Test-Path $lockPath)) {
            if (-not (Test-Path $runnerPath)) {
                Write-Log "Run script not found at '$runnerPath'. Waiting for next cycle."
                Publish-HostSignal -Stage 'WatcherError' -Message "Run script missing at '$runnerPath'."
                Start-Sleep -Seconds $PollSeconds
                continue
            }

            New-Item -Path $lockPath -ItemType File -Force | Out-Null
            Write-Log 'Discovery job detected. Running Run-Discovery.ps1.'
            $detectedJobId = ''
            try {
                if (Test-Path $jobPath) {
                    $jobObj = Get-Content -Path $jobPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    $detectedJobId = [string]$jobObj.JobId
                }
            }
            catch {
                Write-Log "Unable to parse job id: $($_.Exception.Message)"
            }
            Publish-HostSignal -Stage 'JobDetected' -Message 'Watcher detected discovery job and is starting runner.' -JobId $detectedJobId

            try {
                & $runnerPath -JobPath $jobPath
                Write-Log 'Run-Discovery.ps1 execution finished.'
            }
            catch {
                Write-Log "Run-Discovery.ps1 failed: $($_.Exception.Message)"
            }
            finally {
                foreach ($cleanupFile in @($triggerPath, $lockPath)) {
                    if (Test-Path $cleanupFile) {
                        try { Remove-Item -Path $cleanupFile -Force -ErrorAction SilentlyContinue } catch {}
                    }
                }
                Publish-HostSignal -Stage 'WatcherReady' -Message 'Watcher is idle and waiting for next job.'
            }
        }
        elseif (-not (Test-Path $lockPath)) {
            Publish-HostSignal -Stage 'WatcherReady' -Message 'Watcher is idle and waiting for next job.'
        }
    }
    catch {
        Write-Log "Watcher loop error: $($_.Exception.Message)"
        Publish-HostSignal -Stage 'WatcherError' -Message $_.Exception.Message
    }

    Start-Sleep -Seconds $PollSeconds
}
