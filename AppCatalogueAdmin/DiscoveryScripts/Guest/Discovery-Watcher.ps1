param(
    [int]$PollSeconds = 4
)

$ErrorActionPreference = 'Stop'

$discoveryRoot = 'C:\Discovery'
$scriptsDir = Join-Path $discoveryRoot 'Scripts'
$loggingHelperPath = Join-Path $scriptsDir 'Discovery-Logging.ps1'

if (-not (Test-Path $loggingHelperPath)) {
    throw "Discovery logging helper was not found: $loggingHelperPath"
}

. $loggingHelperPath

$layout = New-DiscoveryLayout -DiscoveryRoot $discoveryRoot
$jobPath = Join-Path $layout.InputDir 'job.json'
$triggerPath = Join-Path $layout.InputDir 'run.trigger'
$lockPath = Join-Path $layout.InputDir 'processing.lock'
$runnerPath = Join-Path $layout.ScriptsDir 'Run-Discovery.ps1'
$lastPublishedSignal = ''
$suppressIdleSignalUntilUtc = Get-Date

function Publish-WatcherSignal {
    param(
        [string]$Stage,
        [string]$Message,
        [string]$JobId = '',
        [string]$State = 'InProgress',
        [bool]$ResultReady = $false,
        [Nullable[bool]]$Success = $null,
        [string]$Error = '',
        [string]$ResultPath = '',
        [string]$LogPath = ''
    )

    $fingerprint = "$Stage|$Message|$JobId|$State|$ResultReady|$Error|$ResultPath|$LogPath"
    if ($fingerprint -eq $script:lastPublishedSignal) {
        return
    }

    $script:lastPublishedSignal = $fingerprint
    Publish-DiscoveryHostSignal `
        -Layout $layout `
        -JobId $JobId `
        -Stage $Stage `
        -Message $Message `
        -State $State `
        -ResultReady $ResultReady `
        -Success $Success `
        -Error $Error `
        -ResultPath $ResultPath `
        -LogPath $LogPath
}

Write-WatcherLog -Layout $layout -Message "WatcherStarted PollSeconds=$PollSeconds"
Publish-WatcherSignal -Stage 'WatcherStarted' -Message 'Watcher started and waiting for jobs.' -State 'Waiting'

while ($true) {
    try {
        $hasJob = Test-Path $jobPath
        $hasTrigger = Test-Path $triggerPath

        if (($hasJob -or $hasTrigger) -and -not (Test-Path $lockPath)) {
            if (-not (Test-Path $runnerPath)) {
                $message = "Run-Discovery script missing at '$runnerPath'."
                Write-WatcherLog -Layout $layout -Message "WatcherError $message"
                Publish-WatcherSignal -Stage 'DiscoveryFailed' -Message $message -State 'Failed' -ResultReady $false -Error $message
                Start-Sleep -Seconds $PollSeconds
                continue
            }

            New-Item -Path $lockPath -ItemType File -Force | Out-Null
            $detectedJobId = ''

            try {
                if (Test-Path $jobPath) {
                    $job = Get-Content -Path $jobPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    $detectedJobId = [string]$job.JobId
                }
            }
            catch {
                Write-WatcherLog -Layout $layout -Message ("WatcherWarning Failed to parse job id: {0}" -f $_.Exception.Message)
            }

            $jobLabel = if ([string]::IsNullOrWhiteSpace($detectedJobId)) { '<unknown>' } else { $detectedJobId }
            Write-WatcherLog -Layout $layout -Message "JobDetected JobId=$jobLabel"
            Publish-WatcherSignal -Stage 'JobDetected' -Message "Watcher detected job $jobLabel." -JobId $detectedJobId -State 'InProgress'

            try {
                & $runnerPath -JobPath $jobPath
                Write-WatcherLog -Layout $layout -Message "JobComplete JobId=$jobLabel"
            }
            catch {
                $runnerError = $_.Exception.Message
                Write-WatcherLog -Layout $layout -Message "RunnerError JobId=$jobLabel Error=$runnerError"
                $jobContext = $null
                if (-not [string]::IsNullOrWhiteSpace($detectedJobId)) {
                    try {
                        $jobContext = New-DiscoveryJobContext -JobId $detectedJobId -DiscoveryRoot $discoveryRoot
                    }
                    catch {
                        Write-WatcherLog -Layout $layout -Message "WatcherWarning Could not create job context for failure signal: $($_.Exception.Message)"
                    }
                }
                Publish-WatcherSignal `
                    -Stage 'DiscoveryFailed' `
                    -Message "Runner failure for job $jobLabel." `
                    -JobId $detectedJobId `
                    -State 'Failed' `
                    -Error $runnerError `
                    -ResultReady $true `
                    -Success $false `
                    -ResultPath $(if ($null -eq $jobContext) { '' } else { $jobContext.ResultPath }) `
                    -LogPath $(if ($null -eq $jobContext) { '' } else { $jobContext.GuestLogPath })
            }
            finally {
                $script:suppressIdleSignalUntilUtc = (Get-Date).AddSeconds(120)
                foreach ($cleanupFile in @($triggerPath, $lockPath)) {
                    if (Test-Path $cleanupFile) {
                        try {
                            Remove-Item -Path $cleanupFile -Force -ErrorAction SilentlyContinue
                        }
                        catch {
                            Write-WatcherLog -Layout $layout -Message "CleanupWarning Could not remove '$cleanupFile': $($_.Exception.Message)"
                        }
                    }
                }

            }
        }
        elseif (-not (Test-Path $lockPath)) {
            if ((Get-Date) -ge $script:suppressIdleSignalUntilUtc) {
                Publish-WatcherSignal -Stage 'WatcherStarted' -Message 'Watcher idle and waiting for jobs.' -State 'Waiting'
            }
        }
    }
    catch {
        $errorMessage = $_.Exception.Message
        Write-WatcherLog -Layout $layout -Message "WatcherLoopError $errorMessage"
        Publish-WatcherSignal -Stage 'DiscoveryFailed' -Message 'Watcher loop error.' -State 'Failed' -Error $errorMessage
        $script:suppressIdleSignalUntilUtc = (Get-Date).AddSeconds(60)
    }

    Start-Sleep -Seconds $PollSeconds
}
