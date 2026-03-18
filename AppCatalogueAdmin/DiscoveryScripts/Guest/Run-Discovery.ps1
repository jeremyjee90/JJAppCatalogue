param(
    [string]$JobPath = 'C:\Discovery\Input\job.json'
)

$ErrorActionPreference = 'Stop'

$discoveryRoot = 'C:\Discovery'
$inputDir = Join-Path $discoveryRoot 'Input'
$outputDir = Join-Path $discoveryRoot 'Output'
$logsDir = Join-Path $discoveryRoot 'Logs'
$runLogPath = Join-Path $logsDir 'Run-Discovery.log'
$resultPath = Join-Path $outputDir 'discovery-results.json'
$statusPath = Join-Path $outputDir 'status.json'
$hostSignalRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters'
$global:DiscoveryJobId = ''

New-Item -Path $inputDir -ItemType Directory -Force | Out-Null
New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
New-Item -Path $logsDir -ItemType Directory -Force | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "{0:u} {1}" -f (Get-Date), $Message
    Add-Content -Path $runLogPath -Value $line -Encoding UTF8
}

function Publish-HostSignal {
    param(
        [string]$Stage,
        [string]$Message,
        [bool]$ResultReady = $false
    )

    try {
        New-Item -Path $hostSignalRegistryPath -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryStage' -Value $Stage -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryMessage' -Value $Message -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryResultReady' -Value ($(if ($ResultReady) { 'true' } else { 'false' })) -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryJobId' -Value $global:DiscoveryJobId -Type String -Force
        Set-ItemProperty -Path $hostSignalRegistryPath -Name 'AppCatalogueDiscoveryUpdatedUtc' -Value ((Get-Date).ToUniversalTime().ToString('o')) -Type String -Force
    }
    catch {
        Write-Log "Host signal update failed: $($_.Exception.Message)"
    }
}

function Write-Status {
    param(
        [string]$Stage,
        [string]$Message,
        [int]$Percent = 0,
        [bool]$IsError = $false
    )

    $status = [ordered]@{
        Stage = $Stage
        Message = $Message
        Percent = $Percent
        IsError = $IsError
        TimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
    }

    $status | ConvertTo-Json -Depth 6 | Set-Content -Path $statusPath -Encoding UTF8
    Write-Log "STATUS [$Stage] $Message"
    Publish-HostSignal -Stage $Stage -Message $Message -ResultReady $false
}

function Get-UninstallSnapshot {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    $entries = @()
    foreach ($path in $paths) {
        try {
            $items = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
            foreach ($item in $items) {
                if ([string]::IsNullOrWhiteSpace($item.DisplayName)) {
                    continue
                }

                $entries += [pscustomobject]@{
                    DisplayName = [string]$item.DisplayName
                    RegistryPath = [string]$item.PSPath
                    DisplayVersion = [string]$item.DisplayVersion
                    Publisher = [string]$item.Publisher
                }
            }
        }
        catch {
            Write-Log "Snapshot warning on path '$path': $($_.Exception.Message)"
        }
    }

    return $entries | Sort-Object -Property DisplayName, RegistryPath -Unique
}

function Get-AppTokens {
    param([string]$AppName)

    if ([string]::IsNullOrWhiteSpace($AppName)) {
        return @()
    }

    $tokens = $AppName -split '[\s\-_\.]+' |
        Where-Object { $_ -and $_.Length -ge 3 } |
        ForEach-Object { $_.ToLowerInvariant() } |
        Sort-Object -Unique

    if ($tokens.Count -eq 0) {
        $tokens = @($AppName.Trim().ToLowerInvariant())
    }

    return $tokens
}

function Get-FileSnapshot {
    param([string]$AppName)

    $roots = @()
    $programFiles = $env:ProgramFiles
    $programFilesX86 = ${env:ProgramFiles(x86)}
    $localAppData = $env:LOCALAPPDATA
    $roamingAppData = $env:APPDATA

    foreach ($candidate in @($programFiles, $programFilesX86, $localAppData, $roamingAppData)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            $roots += $candidate
        }
    }

    $roots = $roots | Sort-Object -Unique
    $tokens = Get-AppTokens -AppName $AppName
    $snapshot = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $roots) {
        try {
            $dirs = Get-ChildItem -Path $root -Directory -Recurse -ErrorAction SilentlyContinue
            foreach ($dir in $dirs) {
                $fullName = $dir.FullName
                $lower = $fullName.ToLowerInvariant()
                if ($tokens | Where-Object { $lower.Contains($_) }) {
                    [void]$snapshot.Add($fullName)
                    try {
                        $exes = Get-ChildItem -Path $fullName -File -Filter '*.exe' -ErrorAction SilentlyContinue
                        foreach ($exe in $exes) {
                            [void]$snapshot.Add($exe.FullName)
                        }
                    }
                    catch {
                        # best effort
                    }
                }
            }
        }
        catch {
            Write-Log "File snapshot warning on '$root': $($_.Exception.Message)"
        }
    }

    return @($snapshot)
}

function Invoke-ProcessCapture {
    param(
        [string]$FilePath,
        [string]$Arguments,
        [int]$TimeoutSeconds = 15
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    $result = [ordered]@{
        Arguments = $Arguments
        ExitCode = -1
        TimedOut = $false
        StandardOutput = ''
        StandardError = ''
    }

    try {
        [void]$process.Start()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $result.TimedOut = $true
            try { $process.Kill() } catch {}
            return [pscustomobject]$result
        }

        $result.ExitCode = $process.ExitCode
        $result.StandardOutput = $process.StandardOutput.ReadToEnd()
        $result.StandardError = $process.StandardError.ReadToEnd()
        return [pscustomobject]$result
    }
    finally {
        $process.Dispose()
    }
}

function Get-SilentSwitchCandidatesFromText {
    param([string]$Text)

    $candidates = @()
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $candidates
    }

    $patterns = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/silent', '/quiet', '/qn', '/qb', '/S', '/s')
    foreach ($pattern in $patterns) {
        if ($Text -match [regex]::Escape($pattern)) {
            $candidates += $pattern
        }
    }

    return $candidates | Sort-Object -Unique
}

function Invoke-SilentSwitchProbe {
    param(
        [string]$InstallerPath,
        [string]$InstallerType,
        [int]$TimeoutSeconds = 15
    )

    $probeAttempts = @()
    $suggestions = @()
    $rawOutput = ''

    if ($InstallerType -eq 'Msi') {
        $suggestions += [pscustomobject]@{
            Arguments = '/qn /norestart'
            Confidence = 'High'
            Reason = 'MSI installer detected.'
        }

        $probeAttempts += "MSI default suggestion applied."
        return [pscustomobject]@{
            Suggestions = $suggestions
            ProbeAttempts = $probeAttempts
            RawOutput = $rawOutput
        }
    }

    $helpArguments = @('/?', '/help', '-help', '/h', '-h', '--help', '-?')
    foreach ($arg in $helpArguments) {
        $probe = Invoke-ProcessCapture -FilePath $InstallerPath -Arguments $arg -TimeoutSeconds $TimeoutSeconds
        $combinedOutput = (($probe.StandardOutput + [Environment]::NewLine + $probe.StandardError).Trim())
        $probeAttempts += "Probe '$arg' exit=$($probe.ExitCode) timeout=$($probe.TimedOut)"

        if (-not [string]::IsNullOrWhiteSpace($combinedOutput)) {
            $rawOutput += "=== Probe $arg ===`r`n$combinedOutput`r`n`r`n"
            $candidates = Get-SilentSwitchCandidatesFromText -Text $combinedOutput
            foreach ($candidate in $candidates) {
                $suggestions += [pscustomobject]@{
                    Arguments = $candidate
                    Confidence = if ($candidate -in @('/silent', '/quiet', '/VERYSILENT', '/qn')) { 'High' } else { 'Medium' }
                    Reason = "Detected from help output for probe '$arg'."
                }
            }
        }
    }

    $suggestions = $suggestions |
        Group-Object -Property Arguments |
        ForEach-Object { $_.Group[0] }

    return [pscustomobject]@{
        Suggestions = $suggestions
        ProbeAttempts = $probeAttempts
        RawOutput = $rawOutput.Trim()
    }
}

function Invoke-InstallAttempt {
    param(
        [string]$InstallerPath,
        [string]$InstallerType,
        [string]$PreferredSilentArguments,
        [array]$ProbeSuggestions,
        [int]$TimeoutSeconds
    )

    $attemptLog = @()
    $acceptedCodes = @(0, 3010, 1641)

    if ($InstallerType -eq 'Msi') {
        $arguments = if ([string]::IsNullOrWhiteSpace($PreferredSilentArguments)) { '/qn /norestart' } else { $PreferredSilentArguments.Trim() }
        $msiArgs = "/i `"$InstallerPath`" $arguments"
        $probe = Invoke-ProcessCapture -FilePath 'msiexec.exe' -Arguments $msiArgs -TimeoutSeconds $TimeoutSeconds
        $attemptLog += "msiexec $msiArgs => exit=$($probe.ExitCode) timeout=$($probe.TimedOut)"

        return [pscustomobject]@{
            Success = (-not $probe.TimedOut) -and ($acceptedCodes -contains $probe.ExitCode)
            ExitCode = $probe.ExitCode
            ArgumentsUsed = $arguments
            AttemptLog = $attemptLog
        }
    }

    $candidateArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($PreferredSilentArguments)) {
        $candidateArgs += $PreferredSilentArguments.Trim()
    }

    foreach ($probeSuggestion in $ProbeSuggestions) {
        if (-not [string]::IsNullOrWhiteSpace($probeSuggestion.Arguments)) {
            $candidateArgs += $probeSuggestion.Arguments.Trim()
        }
    }

    $candidateArgs += @('/S', '/silent', '/quiet /norestart', '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART')
    $candidateArgs = $candidateArgs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique

    foreach ($args in $candidateArgs) {
        $probe = Invoke-ProcessCapture -FilePath $InstallerPath -Arguments $args -TimeoutSeconds $TimeoutSeconds
        $attemptLog += "`"$InstallerPath`" $args => exit=$($probe.ExitCode) timeout=$($probe.TimedOut)"

        if ((-not $probe.TimedOut) -and ($acceptedCodes -contains $probe.ExitCode)) {
            return [pscustomobject]@{
                Success = $true
                ExitCode = $probe.ExitCode
                ArgumentsUsed = $args
                AttemptLog = $attemptLog
            }
        }
    }

    return [pscustomobject]@{
        Success = $false
        ExitCode = -1
        ArgumentsUsed = ''
        AttemptLog = $attemptLog
    }
}

try {
    if (-not (Test-Path $JobPath)) {
        throw "Job file not found: $JobPath"
    }

    Write-Status -Stage 'Preparing' -Message 'Loading discovery job.' -Percent 5
    $job = Get-Content -Path $JobPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $global:DiscoveryJobId = [string]$job.JobId
    Publish-HostSignal -Stage 'Preparing' -Message 'Discovery job accepted by guest runner.' -ResultReady $false

    $installerPath = Join-Path $inputDir $job.InstallerFileName
    if (-not (Test-Path $installerPath)) {
        throw "Installer file not found in guest input: $installerPath"
    }

    $installerType = if ($installerPath.ToLowerInvariant().EndsWith('.msi')) { 'Msi' } else { 'Exe' }

    $result = [ordered]@{
        Success = $false
        InstallerType = $installerType
        SilentSwitchSuggestions = @()
        PrimaryDetection = $null
        SecondaryDetection = $null
        Evidence = [ordered]@{
            NewUninstallEntries = @()
            NewFiles = @()
            NewRegistryKeys = @()
            ProbeAttempts = @()
        }
        RawHelpOutput = ''
        InstallAttemptSummary = ''
        Errors = @()
    }

    Write-Status -Stage 'SnapshotBefore' -Message 'Capturing uninstall/file snapshot before install.' -Percent 15
    $beforeUninstall = Get-UninstallSnapshot
    $beforeFiles = Get-FileSnapshot -AppName $job.AppName

    Write-Status -Stage 'ProbeSilentSwitches' -Message 'Probing installer help output.' -Percent 30
    $probe = Invoke-SilentSwitchProbe -InstallerPath $installerPath -InstallerType $installerType -TimeoutSeconds $job.ProbeTimeoutSeconds
    $result.SilentSwitchSuggestions = $probe.Suggestions
    $result.Evidence.ProbeAttempts = $probe.ProbeAttempts
    $result.RawHelpOutput = $probe.RawOutput

    Write-Status -Stage 'InstallAnalysis' -Message 'Running install attempt analysis.' -Percent 50
    $installAttempt = Invoke-InstallAttempt `
        -InstallerPath $installerPath `
        -InstallerType $installerType `
        -PreferredSilentArguments $job.PreferredSilentArguments `
        -ProbeSuggestions $probe.Suggestions `
        -TimeoutSeconds $job.InstallerTimeoutSeconds
    $result.InstallAttemptSummary = ($installAttempt.AttemptLog -join [Environment]::NewLine)

    if (-not $installAttempt.Success) {
        $result.Errors += 'No successful silent install execution was detected.'
    }

    Write-Status -Stage 'SnapshotAfter' -Message 'Capturing uninstall/file snapshot after install.' -Percent 70
    $afterUninstall = Get-UninstallSnapshot
    $afterFiles = Get-FileSnapshot -AppName $job.AppName

    $beforeUninstallMap = @{}
    foreach ($entry in $beforeUninstall) {
        $beforeUninstallMap[$entry.RegistryPath] = $true
    }

    $newUninstallEntries = @()
    foreach ($entry in $afterUninstall) {
        if (-not $beforeUninstallMap.ContainsKey($entry.RegistryPath)) {
            $newUninstallEntries += $entry
        }
    }

    $beforeFileMap = @{}
    foreach ($path in $beforeFiles) {
        $beforeFileMap[$path] = $true
    }

    $newFiles = @()
    foreach ($path in $afterFiles) {
        if (-not $beforeFileMap.ContainsKey($path)) {
            $newFiles += $path
        }
    }

    $newFiles = $newFiles | Sort-Object -Unique
    $newRegistryKeys = $newUninstallEntries | Select-Object -ExpandProperty RegistryPath -Unique
    $newDisplayNames = $newUninstallEntries | Select-Object -ExpandProperty DisplayName -Unique

    $result.Evidence.NewUninstallEntries = @($newDisplayNames)
    $result.Evidence.NewFiles = @($newFiles | Select-Object -First 50)
    $result.Evidence.NewRegistryKeys = @($newRegistryKeys)

    if ($newDisplayNames.Count -gt 0) {
        $result.PrimaryDetection = [ordered]@{
            Type = 'RegistryDisplayName'
            Value = [string]$newDisplayNames[0]
            Confidence = 'High'
            Reason = 'New uninstall entry detected after install.'
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($job.AppName)) {
        $result.PrimaryDetection = [ordered]@{
            Type = 'RegistryDisplayName'
            Value = [string]$job.AppName
            Confidence = 'Medium'
            Reason = 'No new uninstall entry found; using app name fallback.'
        }
    }

    $primaryExePath = $newFiles |
        Where-Object { $_ -and $_.ToLowerInvariant().EndsWith('.exe') } |
        Select-Object -First 1

    if (-not [string]::IsNullOrWhiteSpace($primaryExePath)) {
        $result.SecondaryDetection = [ordered]@{
            Type = 'FileExists'
            Value = [string]$primaryExePath
            Confidence = 'High'
            Reason = 'New executable path detected after install.'
        }
    }
    elseif ($newRegistryKeys.Count -gt 0) {
        $result.SecondaryDetection = [ordered]@{
            Type = 'RegistryKeyExists'
            Value = [string]$newRegistryKeys[0]
            Confidence = 'Medium'
            Reason = 'New uninstall registry key detected after install.'
        }
    }

    if ($result.SilentSwitchSuggestions.Count -eq 0 -and $installerType -eq 'Msi') {
        $result.SilentSwitchSuggestions += [ordered]@{
            Arguments = '/qn /norestart'
            Confidence = 'High'
            Reason = 'MSI installer default silent arguments.'
        }
    }

    $result.Success = $installAttempt.Success -or $result.PrimaryDetection -ne $null -or $result.SecondaryDetection -ne $null

    Write-Status -Stage 'WritingResults' -Message 'Writing discovery result JSON.' -Percent 90
    $result | ConvertTo-Json -Depth 8 | Set-Content -Path $resultPath -Encoding UTF8
    Publish-HostSignal -Stage 'WritingResults' -Message 'Result file written in guest output.' -ResultReady $true

    Write-Status -Stage 'Complete' -Message 'Discovery run completed.' -Percent 100
    Publish-HostSignal -Stage 'Complete' -Message 'Discovery run completed successfully.' -ResultReady $true
    Write-Log "Discovery complete. Success=$($result.Success)"

    if ($job.ShutdownVmOnComplete -eq $true) {
        Write-Log 'Shutdown requested by discovery job. Powering off guest.'
        Start-Sleep -Seconds 2
        Stop-Computer -Force
    }
}
catch {
    $message = $_.Exception.Message
    Write-Log "Discovery failed: $message"

    $fallbackResult = [ordered]@{
        Success = $false
        InstallerType = 'Unknown'
        SilentSwitchSuggestions = @()
        PrimaryDetection = $null
        SecondaryDetection = $null
        Evidence = [ordered]@{
            NewUninstallEntries = @()
            NewFiles = @()
            NewRegistryKeys = @()
            ProbeAttempts = @()
        }
        RawHelpOutput = ''
        InstallAttemptSummary = ''
        Errors = @($message)
    }

    $fallbackResult | ConvertTo-Json -Depth 8 | Set-Content -Path $resultPath -Encoding UTF8
    Write-Status -Stage 'Failed' -Message $message -Percent 100 -IsError $true
    Publish-HostSignal -Stage 'Failed' -Message $message -ResultReady $true

    if (Test-Path $JobPath) {
        try { Remove-Item -Path $JobPath -Force -ErrorAction SilentlyContinue } catch {}
    }

    throw
}
