Set-StrictMode -Version Latest

function New-DiscoveryLayout {
    param(
        [string]$DiscoveryRoot = 'C:\Discovery'
    )

    $inputDir = Join-Path $DiscoveryRoot 'Input'
    $outputDir = Join-Path $DiscoveryRoot 'Output'
    $jobsOutputDir = Join-Path $outputDir 'Jobs'
    $scriptsDir = Join-Path $DiscoveryRoot 'Scripts'
    $logsDir = Join-Path $DiscoveryRoot 'Logs'

    foreach ($path in @($inputDir, $outputDir, $jobsOutputDir, $scriptsDir, $logsDir)) {
        New-Item -Path $path -ItemType Directory -Force | Out-Null
    }

    [pscustomobject]@{
        DiscoveryRoot = $DiscoveryRoot
        InputDir = $inputDir
        OutputDir = $outputDir
        JobsOutputDir = $jobsOutputDir
        ScriptsDir = $scriptsDir
        LogsDir = $logsDir
        WatcherLogPath = Join-Path $logsDir 'Discovery-Watcher.log'
        HostSignalRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters'
    }
}

function New-DiscoveryJobContext {
    param(
        [Parameter(Mandatory = $true)]
        [string]$JobId,
        [string]$DiscoveryRoot = 'C:\Discovery'
    )

    $layout = New-DiscoveryLayout -DiscoveryRoot $DiscoveryRoot
    $jobOutputDir = Join-Path $layout.JobsOutputDir $JobId
    New-Item -Path $jobOutputDir -ItemType Directory -Force | Out-Null

    [pscustomobject]@{
        JobId = $JobId
        DiscoveryRoot = $DiscoveryRoot
        Layout = $layout
        JobOutputDir = $jobOutputDir
        GuestLogPath = Join-Path $jobOutputDir 'guest.log'
        StatusPath = Join-Path $jobOutputDir 'discovery-status.json'
        ResultPath = Join-Path $jobOutputDir 'discovery-results.json'
    }
}

function Write-WatcherLog {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Layout,
        [string]$Message
    )

    $line = "{0:o} [INFO] [Watcher] {1}" -f (Get-Date).ToUniversalTime(), $Message
    Add-Content -Path $Layout.WatcherLogPath -Value $line -Encoding UTF8
}

function Write-DiscoveryLog {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [string]$Stage,
        [string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')]
        [string]$Severity = 'INFO'
    )

    $safeStage = if ([string]::IsNullOrWhiteSpace($Stage)) { 'General' } else { $Stage }
    $line = "{0:o} [{1}] [{2}] {3}" -f (Get-Date).ToUniversalTime(), $Severity, $safeStage, $Message
    Add-Content -Path $Context.GuestLogPath -Value $line -Encoding UTF8
}

function Write-DiscoveryStatus {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [string]$State,
        [string]$Stage,
        [string]$Message,
        [Nullable[bool]]$Success = $null,
        [string]$Error = '',
        [string]$ResultPath = '',
        [string]$LogPath = ''
    )

    $status = [ordered]@{
        jobId = $Context.JobId
        success = if ($null -eq $Success) { $null } else { [bool]$Success }
        state = if ([string]::IsNullOrWhiteSpace($State)) { 'InProgress' } else { $State }
        stage = if ([string]::IsNullOrWhiteSpace($Stage)) { 'Unknown' } else { $Stage }
        message = if ($null -eq $Message) { '' } else { $Message }
        error = if ($null -eq $Error) { '' } else { $Error }
        updatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        resultPath = if ([string]::IsNullOrWhiteSpace($ResultPath)) { $Context.ResultPath } else { $ResultPath }
        logPath = if ([string]::IsNullOrWhiteSpace($LogPath)) { $Context.GuestLogPath } else { $LogPath }
    }

    $status | ConvertTo-Json -Depth 8 | Set-Content -Path $Context.StatusPath -Encoding UTF8
}

function Publish-DiscoveryHostSignal {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Layout,
        [string]$JobId = '',
        [string]$Stage = '',
        [string]$Message = '',
        [string]$State = 'InProgress',
        [bool]$ResultReady = $false,
        [Nullable[bool]]$Success = $null,
        [string]$Error = '',
        [string]$ResultPath = '',
        [string]$LogPath = ''
    )

    try {
        New-Item -Path $Layout.HostSignalRegistryPath -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryStage' -Value $Stage -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryMessage' -Value $Message -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryState' -Value $State -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryResultReady' -Value ($(if ($ResultReady) { 'true' } else { 'false' })) -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryJobId' -Value $JobId -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoverySuccess' -Value ($(if ($null -eq $Success) { '' } elseif ([bool]$Success) { 'true' } else { 'false' })) -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryError' -Value $Error -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryResultPath' -Value $ResultPath -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryLogPath' -Value $LogPath -Type String -Force
        Set-ItemProperty -Path $Layout.HostSignalRegistryPath -Name 'AppCatalogueDiscoveryUpdatedUtc' -Value ((Get-Date).ToUniversalTime().ToString('o')) -Type String -Force
    }
    catch {
        Write-WatcherLog -Layout $Layout -Message ("Host signal update failed: {0}" -f $_.Exception.Message)
    }
}

function Update-DiscoveryStage {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Context,
        [string]$Stage,
        [string]$Message,
        [string]$State = 'InProgress',
        [ValidateSet('INFO', 'WARN', 'ERROR')]
        [string]$Severity = 'INFO',
        [bool]$ResultReady = $false,
        [Nullable[bool]]$Success = $null,
        [string]$Error = '',
        [string]$ResultPath = '',
        [string]$LogPath = ''
    )

    $resolvedResultPath = if ([string]::IsNullOrWhiteSpace($ResultPath)) { $Context.ResultPath } else { $ResultPath }
    $resolvedLogPath = if ([string]::IsNullOrWhiteSpace($LogPath)) { $Context.GuestLogPath } else { $LogPath }

    Write-DiscoveryLog -Context $Context -Stage $Stage -Message $Message -Severity $Severity
    Write-DiscoveryStatus `
        -Context $Context `
        -State $State `
        -Stage $Stage `
        -Message $Message `
        -Success $Success `
        -Error $Error `
        -ResultPath $resolvedResultPath `
        -LogPath $resolvedLogPath
    Publish-DiscoveryHostSignal `
        -Layout $Context.Layout `
        -JobId $Context.JobId `
        -Stage $Stage `
        -Message $Message `
        -State $State `
        -ResultReady $ResultReady `
        -Success $Success `
        -Error $Error `
        -ResultPath $resolvedResultPath `
        -LogPath $resolvedLogPath
}
