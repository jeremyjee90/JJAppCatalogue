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

New-Item -Path $inputDir -ItemType Directory -Force | Out-Null
New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
New-Item -Path $scriptsDir -ItemType Directory -Force | Out-Null
New-Item -Path $logsDir -ItemType Directory -Force | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "{0:u} {1}" -f (Get-Date), $Message
    Add-Content -Path $watcherLogPath -Value $line -Encoding UTF8
}

Write-Log "Watcher started. PollSeconds=$PollSeconds"

while ($true) {
    try {
        $hasJob = Test-Path $jobPath
        $hasTrigger = Test-Path $triggerPath

        if (($hasJob -or $hasTrigger) -and -not (Test-Path $lockPath)) {
            if (-not (Test-Path $runnerPath)) {
                Write-Log "Run script not found at '$runnerPath'. Waiting for next cycle."
                Start-Sleep -Seconds $PollSeconds
                continue
            }

            New-Item -Path $lockPath -ItemType File -Force | Out-Null
            Write-Log 'Discovery job detected. Running Run-Discovery.ps1.'

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
            }
        }
    }
    catch {
        Write-Log "Watcher loop error: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds $PollSeconds
}
