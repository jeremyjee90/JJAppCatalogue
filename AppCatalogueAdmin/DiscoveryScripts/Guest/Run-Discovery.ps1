param(
    [string]$JobPath = 'C:\Discovery\Input\job.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$discoveryRoot = 'C:\Discovery'
$scriptsDir = Join-Path $discoveryRoot 'Scripts'
$loggingHelperPath = Join-Path $scriptsDir 'Discovery-Logging.ps1'

if (-not (Test-Path $loggingHelperPath)) {
    throw "Discovery logging helper was not found: $loggingHelperPath"
}

. $loggingHelperPath

$layout = New-DiscoveryLayout -DiscoveryRoot $discoveryRoot
$inputDir = $layout.InputDir

function Get-AppTokens {
    param([string]$AppName)

    if ([string]::IsNullOrWhiteSpace($AppName)) {
        return @()
    }

    $tokens = @()
    foreach ($token in ($AppName -split '[\s\-_\.]+' | Where-Object { $_ -and $_.Length -ge 3 })) {
        $tokens += $token.Trim().ToLowerInvariant()
    }

    if ($tokens.Count -eq 0) {
        $tokens = @($AppName.Trim().ToLowerInvariant())
    }

    return $tokens | Sort-Object -Unique
}

function Test-TokenMatch {
    param(
        [string]$Text,
        [string[]]$Tokens
    )

    if ([string]::IsNullOrWhiteSpace($Text) -or $null -eq $Tokens -or $Tokens.Count -eq 0) {
        return $false
    }

    $lower = $Text.ToLowerInvariant()
    foreach ($token in $Tokens) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }

        if ($lower.Contains($token.ToLowerInvariant())) {
            return $true
        }
    }

    return $false
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
            # Best effort snapshot only.
        }
    }

    return $entries | Sort-Object -Property DisplayName, RegistryPath -Unique
}

function Get-ServiceSnapshot {
    try {
        return Get-Service | ForEach-Object {
            [pscustomobject]@{
                Name = [string]$_.Name
                DisplayName = [string]$_.DisplayName
            }
        }
    }
    catch {
        return @()
    }
}

function Get-ProcessSnapshot {
    try {
        return Get-Process | Select-Object -ExpandProperty ProcessName -Unique
    }
    catch {
        return @()
    }
}

function Get-InterestingDirectories {
    param(
        [string]$RootPath,
        [string[]]$Tokens
    )

    $collected = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ([string]::IsNullOrWhiteSpace($RootPath) -or -not (Test-Path $RootPath)) {
        return @()
    }

    try {
        $topDirectories = Get-ChildItem -Path $RootPath -Directory -ErrorAction SilentlyContinue
        foreach ($top in $topDirectories) {
            if (Test-TokenMatch -Text $top.FullName -Tokens $Tokens) {
                [void]$collected.Add($top.FullName)
                continue
            }

            try {
                $childDirectories = Get-ChildItem -Path $top.FullName -Directory -ErrorAction SilentlyContinue | Select-Object -First 120
                foreach ($child in $childDirectories) {
                    if (Test-TokenMatch -Text $child.FullName -Tokens $Tokens) {
                        [void]$collected.Add($child.FullName)
                    }
                }
            }
            catch {
                # Ignore per-directory access issues.
            }
        }
    }
    catch {
        return @()
    }

    return @($collected)
}

function Get-FileSnapshot {
    param([string]$AppName)

    $tokens = Get-AppTokens -AppName $AppName
    $roots = @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:LOCALAPPDATA,
        $env:APPDATA
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) } | Sort-Object -Unique

    $snapshot = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $roots) {
        $interesting = Get-InterestingDirectories -RootPath $root -Tokens $tokens
        foreach ($directory in $interesting) {
            [void]$snapshot.Add($directory)

            try {
                $exeFiles = Get-ChildItem -Path $directory -File -Filter '*.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 80
                foreach ($exe in $exeFiles) {
                    [void]$snapshot.Add($exe.FullName)
                }
            }
            catch {
                # Best effort.
            }
        }
    }

    return @($snapshot)
}

function Get-ShortcutSnapshot {
    param([string]$AppName)

    $tokens = Get-AppTokens -AppName $AppName
    $locations = @(
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs",
        "$env:APPDATA\Microsoft\Windows\Start Menu\Programs",
        "$env:PUBLIC\Desktop",
        "$env:USERPROFILE\Desktop"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }

    $snapshot = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($location in $locations) {
        try {
            $links = Get-ChildItem -Path $location -Filter '*.lnk' -File -Recurse -ErrorAction SilentlyContinue
            foreach ($link in $links) {
                if (Test-TokenMatch -Text $link.FullName -Tokens $tokens) {
                    [void]$snapshot.Add($link.FullName)
                }
            }
        }
        catch {
            # Best effort.
        }
    }

    return @($snapshot)
}

function New-SystemSnapshot {
    param([string]$AppName)

    [pscustomobject]@{
        UninstallEntries = @(Get-UninstallSnapshot)
        Files = @(Get-FileSnapshot -AppName $AppName)
        Services = @(Get-ServiceSnapshot)
        Shortcuts = @(Get-ShortcutSnapshot -AppName $AppName)
        Processes = @(Get-ProcessSnapshot)
    }
}

function Get-SnapshotDiff {
    param(
        [pscustomobject]$Before,
        [pscustomobject]$After
    )

    $beforeUninstallMap = @{}
    foreach ($entry in $Before.UninstallEntries) {
        $beforeUninstallMap[[string]$entry.RegistryPath] = $true
    }

    $newUninstallEntries = @()
    foreach ($entry in $After.UninstallEntries) {
        $registryPath = [string]$entry.RegistryPath
        if (-not $beforeUninstallMap.ContainsKey($registryPath)) {
            $newUninstallEntries += $entry
        }
    }

    $beforeFilesMap = @{}
    foreach ($item in $Before.Files) {
        $beforeFilesMap[[string]$item] = $true
    }

    $newFiles = @()
    foreach ($item in $After.Files) {
        $path = [string]$item
        if (-not $beforeFilesMap.ContainsKey($path)) {
            $newFiles += $path
        }
    }

    $beforeServicesMap = @{}
    foreach ($service in $Before.Services) {
        $beforeServicesMap[[string]$service.Name] = $true
    }

    $newServices = @()
    foreach ($service in $After.Services) {
        $serviceName = [string]$service.Name
        if (-not $beforeServicesMap.ContainsKey($serviceName)) {
            $newServices += [string]$service.DisplayName
        }
    }

    $beforeShortcutsMap = @{}
    foreach ($shortcut in $Before.Shortcuts) {
        $beforeShortcutsMap[[string]$shortcut] = $true
    }

    $newShortcuts = @()
    foreach ($shortcut in $After.Shortcuts) {
        $shortcutPath = [string]$shortcut
        if (-not $beforeShortcutsMap.ContainsKey($shortcutPath)) {
            $newShortcuts += $shortcutPath
        }
    }

    $beforeProcessesMap = @{}
    foreach ($processName in $Before.Processes) {
        $beforeProcessesMap[[string]$processName] = $true
    }

    $newProcesses = @()
    foreach ($processName in $After.Processes) {
        $name = [string]$processName
        if (-not $beforeProcessesMap.ContainsKey($name)) {
            $newProcesses += $name
        }
    }

    $displayNames = @($newUninstallEntries | Select-Object -ExpandProperty DisplayName -Unique)
    $registryKeys = @($newUninstallEntries | Select-Object -ExpandProperty RegistryPath -Unique)

    [pscustomobject]@{
        NewUninstallEntries = @($displayNames)
        NewUninstallEntriesDetailed = @($newUninstallEntries)
        NewFiles = @($newFiles | Sort-Object -Unique)
        NewRegistryKeys = @($registryKeys)
        NewServices = @($newServices | Sort-Object -Unique)
        NewShortcuts = @($newShortcuts | Sort-Object -Unique)
        NewProcesses = @($newProcesses | Sort-Object -Unique)
    }
}

function Invoke-ProcessCapture {
    param(
        [string]$FilePath,
        [string]$Arguments,
        [int]$TimeoutSeconds
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $result = [ordered]@{
        Started = $false
        TimedOut = $false
        Completed = $false
        ExitCode = $null
        StandardOutput = ''
        StandardError = ''
        Error = ''
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    try {
        $result.Started = $process.Start()
        if (-not $result.Started) {
            $result.Error = 'Process failed to start.'
            return [pscustomobject]$result
        }

        if (-not $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)) {
            $result.TimedOut = $true
            try {
                $process.Kill()
            }
            catch {
                # Best effort kill.
            }

            return [pscustomobject]$result
        }

        $result.Completed = $true
        $result.ExitCode = $process.ExitCode
        $result.StandardOutput = $process.StandardOutput.ReadToEnd()
        $result.StandardError = $process.StandardError.ReadToEnd()
        return [pscustomobject]$result
    }
    catch {
        $result.Error = $_.Exception.Message
        return [pscustomobject]$result
    }
    finally {
        $process.Dispose()
    }
}

function Get-SilentCandidatesFromText {
    param([string]$Text)

    $candidates = @()
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $candidates
    }

    if ($Text -match '(?i)/VERYSILENT') {
        $candidates += [pscustomobject]@{
            Arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
            Source = 'HelpProbe'
            Reason = 'Help output contained /VERYSILENT.'
            BaseConfidence = 0.90
        }
    }

    if ($Text -match '(?i)(^|\s)/S(\s|$)' -or $Text -match '(?i)(^|\s)/s(\s|$)') {
        $candidates += [pscustomobject]@{
            Arguments = '/S'
            Source = 'HelpProbe'
            Reason = 'Help output indicates /S style silent mode.'
            BaseConfidence = 0.70
        }
    }

    if ($Text -match '(?i)/silent') {
        $candidates += [pscustomobject]@{
            Arguments = '/silent /norestart'
            Source = 'HelpProbe'
            Reason = 'Help output indicates /silent.'
            BaseConfidence = 0.80
        }
    }

    if ($Text -match '(?i)/quiet') {
        $candidates += [pscustomobject]@{
            Arguments = '/quiet /norestart'
            Source = 'HelpProbe'
            Reason = 'Help output indicates /quiet.'
            BaseConfidence = 0.80
        }
    }

    if ($Text -match '(?i)\b/qn\b') {
        $candidates += [pscustomobject]@{
            Arguments = '/qn /norestart'
            Source = 'HelpProbe'
            Reason = 'Help output indicates /qn.'
            BaseConfidence = 0.85
        }
    }

    return $candidates
}

function Invoke-HelpProbes {
    param(
        [string]$InstallerPath,
        [int]$TimeoutSeconds
    )

    $probeArguments = @('/?', '/help', '-help', '/h', '-h', '--help', '-?')
    $probeAttempts = @()
    $rawOutput = New-Object System.Text.StringBuilder
    $helpCandidates = @()

    foreach ($probeArgument in $probeArguments) {
        $probeResult = Invoke-ProcessCapture -FilePath $InstallerPath -Arguments $probeArgument -TimeoutSeconds $TimeoutSeconds
        $probeAttempts += "Probe '$probeArgument' Started=$($probeResult.Started) TimedOut=$($probeResult.TimedOut) ExitCode=$($probeResult.ExitCode)"

        $combined = (($probeResult.StandardOutput + [Environment]::NewLine + $probeResult.StandardError).Trim())
        if (-not [string]::IsNullOrWhiteSpace($combined)) {
            [void]$rawOutput.AppendLine("=== Probe: $probeArgument ===")
            [void]$rawOutput.AppendLine($combined)
            [void]$rawOutput.AppendLine()
            $helpCandidates += Get-SilentCandidatesFromText -Text $combined
        }
    }

    $dedupedCandidates = @()
    $seenArgs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $helpCandidates) {
        if ($seenArgs.Add([string]$candidate.Arguments)) {
            $dedupedCandidates += $candidate
        }
    }

    [pscustomobject]@{
        ProbeAttempts = $probeAttempts
        RawOutput = $rawOutput.ToString().Trim()
        Candidates = $dedupedCandidates
    }
}

function Get-InstallerFingerprint {
    param(
        [string]$InstallerPath,
        [string]$HelpOutput
    )

    $extension = [IO.Path]::GetExtension($InstallerPath).ToLowerInvariant()
    if ($extension -eq '.msi') {
        return [pscustomobject]@{
            Family = 'MSI'
            Signature = 'MSI'
            ProductName = ''
            CompanyName = ''
            FileDescription = ''
        }
    }

    $productName = ''
    $companyName = ''
    $fileDescription = ''
    try {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
        $productName = [string]$versionInfo.ProductName
        $companyName = [string]$versionInfo.CompanyName
        $fileDescription = [string]$versionInfo.FileDescription
    }
    catch {
        # Best effort metadata read.
    }

    $combined = ($HelpOutput + ' ' + $productName + ' ' + $companyName + ' ' + $fileDescription).ToLowerInvariant()
    $family = 'UnknownExe'
    if ($combined.Contains('inno setup') -or $combined.Contains('/verysilent') -or $combined.Contains('/sp-')) {
        $family = 'InnoSetup'
    }
    elseif ($combined.Contains('nsis') -or $combined.Contains('nullsoft')) {
        $family = 'NSIS'
    }
    elseif ($combined.Contains('installshield') -or $combined.Contains('isscript')) {
        $family = 'InstallShield'
    }
    elseif ($combined.Contains('wix') -or $combined.Contains('burn bootstrapper') -or $combined.Contains('bootstrapper application')) {
        $family = 'WixBurn'
    }

    [pscustomobject]@{
        Family = $family
        Signature = $family
        ProductName = $productName
        CompanyName = $companyName
        FileDescription = $fileDescription
    }
}

function Add-Candidate {
    param(
        [System.Collections.Generic.List[object]]$Candidates,
        [System.Collections.Generic.HashSet[string]]$Seen,
        [string]$Arguments,
        [string]$Source,
        [string]$Reason,
        [double]$BaseConfidence
    )

    if ([string]::IsNullOrWhiteSpace($Arguments)) {
        return
    }

    $trimmed = $Arguments.Trim()
    if (-not $Seen.Add($trimmed)) {
        return
    }

    $Candidates.Add([pscustomobject]@{
            Arguments = $trimmed
            Source = $Source
            Reason = $Reason
            BaseConfidence = $BaseConfidence
        })
}

function Build-SilentCandidatePlan {
    param(
        [string]$InstallerType,
        [string]$PreferredSilentArguments,
        [pscustomobject]$Fingerprint,
        [object[]]$HelpCandidates
    )

    $candidates = New-Object 'System.Collections.Generic.List[object]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    if ($InstallerType -eq 'Msi') {
        Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/qn /norestart' -Source 'MsiStandard' -Reason 'MSI standard silent install.' -BaseConfidence 0.95
        if (-not [string]::IsNullOrWhiteSpace($PreferredSilentArguments)) {
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments $PreferredSilentArguments -Source 'PreferredInput' -Reason 'Provided by app entry.' -BaseConfidence 0.70
        }

        return $candidates.ToArray()
    }

    foreach ($helpCandidate in $HelpCandidates) {
        Add-Candidate `
            -Candidates $candidates `
            -Seen $seen `
            -Arguments ([string]$helpCandidate.Arguments) `
            -Source ([string]$helpCandidate.Source) `
            -Reason ([string]$helpCandidate.Reason) `
            -BaseConfidence ([double]$helpCandidate.BaseConfidence)
    }

    if (-not [string]::IsNullOrWhiteSpace($PreferredSilentArguments)) {
        Add-Candidate -Candidates $candidates -Seen $seen -Arguments $PreferredSilentArguments -Source 'PreferredInput' -Reason 'Provided by app entry.' -BaseConfidence 0.65
    }

    switch ($Fingerprint.Family) {
        'InnoSetup' {
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -Source 'Fingerprint' -Reason 'Inno Setup fallback profile.' -BaseConfidence 0.82
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -Source 'Fingerprint' -Reason 'Inno Setup alternate silent profile.' -BaseConfidence 0.76
        }
        'NSIS' {
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/S' -Source 'Fingerprint' -Reason 'NSIS fallback profile.' -BaseConfidence 0.74
        }
        'InstallShield' {
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/s /v"/qn /norestart"' -Source 'Fingerprint' -Reason 'InstallShield with embedded MSI quiet args.' -BaseConfidence 0.72
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/s' -Source 'Fingerprint' -Reason 'InstallShield basic silent profile.' -BaseConfidence 0.66
        }
        'WixBurn' {
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/quiet /norestart' -Source 'Fingerprint' -Reason 'WiX/Burn quiet profile.' -BaseConfidence 0.72
            Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/passive /norestart' -Source 'Fingerprint' -Reason 'WiX/Burn passive profile.' -BaseConfidence 0.60
        }
    }

    Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/quiet /norestart' -Source 'GenericFallback' -Reason 'Generic quiet fallback.' -BaseConfidence 0.52
    Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/silent /norestart' -Source 'GenericFallback' -Reason 'Generic silent fallback.' -BaseConfidence 0.50
    Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/S' -Source 'GenericFallback' -Reason 'Generic /S fallback.' -BaseConfidence 0.45
    Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/s' -Source 'GenericFallback' -Reason 'Generic /s fallback.' -BaseConfidence 0.42
    Add-Candidate -Candidates $candidates -Seen $seen -Arguments '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -Source 'GenericFallback' -Reason 'Generic Inno-style fallback.' -BaseConfidence 0.44

    return $candidates.ToArray()
}

function Get-ConfidenceLabel {
    param([double]$Score)

    if ($Score -ge 0.80) { return 'High' }
    if ($Score -ge 0.55) { return 'Medium' }
    return 'Low'
}

function Clamp-Confidence {
    param([double]$Score)
    return [Math]::Max(0.0, [Math]::Min(0.99, $Score))
}

$job = $null
$context = $null

try {
    if (-not (Test-Path $JobPath)) {
        throw "Job file not found: $JobPath"
    }

    $job = Get-Content -Path $JobPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $jobId = [string]$job.JobId
    if ([string]::IsNullOrWhiteSpace($jobId)) {
        throw 'Job JSON is missing JobId.'
    }

    $context = New-DiscoveryJobContext -JobId $jobId -DiscoveryRoot $discoveryRoot
    Update-DiscoveryStage -Context $context -Stage 'JobLoaded' -State 'InProgress' -Message "Discovery job loaded for '$($job.AppName)'."

    $installerPath = Join-Path $inputDir ([string]$job.InstallerFileName)
    if (-not (Test-Path $installerPath)) {
        throw "Installer file not found in guest input: $installerPath"
    }

    Update-DiscoveryStage -Context $context -Stage 'InstallerFound' -State 'InProgress' -Message "Installer located: $installerPath"
    Update-DiscoveryStage -Context $context -Stage 'DiscoveryStarted' -State 'InProgress' -Message 'Starting silent-switch and detection analysis.'

    $installerType = if ($installerPath.ToLowerInvariant().EndsWith('.msi')) { 'Msi' } else { 'Exe' }
    $appName = [string]$job.AppName
    $probeTimeout = [int][Math]::Max(5, [int]$job.ProbeTimeoutSeconds)
    $installerTimeout = [int][Math]::Max(60, [int]$job.InstallerTimeoutSeconds)
    $preferredArgs = [string]$job.PreferredSilentArguments

    $baselineSnapshot = New-SystemSnapshot -AppName $appName

    Update-DiscoveryStage -Context $context -Stage 'SilentSwitchAnalysisStarted' -State 'InProgress' -Message 'Evaluating silent switch candidates.'

    $probeAttempts = @()
    $rawHelpOutput = ''
    $helpCandidates = @()

    if ($installerType -eq 'Exe') {
        $probeResult = Invoke-HelpProbes -InstallerPath $installerPath -TimeoutSeconds $probeTimeout
        $probeAttempts = $probeResult.ProbeAttempts
        $rawHelpOutput = $probeResult.RawOutput
        $helpCandidates = $probeResult.Candidates
    }
    else {
        $probeAttempts += 'MSI detected. Help probe skipped.'
    }

    $fingerprint = Get-InstallerFingerprint -InstallerPath $installerPath -HelpOutput $rawHelpOutput
    $candidatePlan = @(Build-SilentCandidatePlan `
            -InstallerType $installerType `
            -PreferredSilentArguments $preferredArgs `
            -Fingerprint $fingerprint `
            -HelpCandidates $helpCandidates)

    $attemptHistory = @()
    $acceptedExitCodes = @(0, 3010, 1641)
    $winningAttempt = $null
    $winningDiff = $null
    $lastDiff = $null

    for ($index = 0; $index -lt $candidatePlan.Count; $index++) {
        $candidate = $candidatePlan[$index]
        $attemptNumber = $index + 1
        $attemptStageMessage = "Attempt $attemptNumber/$($candidatePlan.Count): $($candidate.Arguments) [$($candidate.Source)]"
        Update-DiscoveryStage -Context $context -Stage 'SilentSwitchAttempt' -State 'InProgress' -Message $attemptStageMessage

        $command = $installerPath
        $arguments = [string]$candidate.Arguments
        if ($installerType -eq 'Msi') {
            $command = 'msiexec.exe'
            $arguments = "/i `"$installerPath`" $arguments"
        }

        $execution = Invoke-ProcessCapture -FilePath $command -Arguments $arguments -TimeoutSeconds $installerTimeout
        Start-Sleep -Seconds 5

        $afterAttemptSnapshot = New-SystemSnapshot -AppName $appName
        $diff = Get-SnapshotDiff -Before $baselineSnapshot -After $afterAttemptSnapshot
        $lastDiff = $diff

        $newUninstallCount = [int]$diff.NewUninstallEntries.Count
        $newFilesCount = [int]$diff.NewFiles.Count
        $newServicesCount = [int]$diff.NewServices.Count
        $newShortcutsCount = [int]$diff.NewShortcuts.Count
        $newProcessesCount = [int]$diff.NewProcesses.Count
        $artifactDetected = ($newUninstallCount + $newFilesCount + $newServicesCount + $newShortcutsCount + $newProcessesCount) -gt 0

        $exitCode = $execution.ExitCode
        $acceptedExit = $false
        if ($null -ne $exitCode) {
            $acceptedExit = $acceptedExitCodes -contains [int]$exitCode
        }

        $rebootRequired = $acceptedExit -and ($exitCode -in @(3010, 1641))
        $pass = $execution.Started -and -not $execution.TimedOut -and $execution.Completed -and (($acceptedExit -and ($artifactDetected -or $installerType -eq 'Msi')) -or ($artifactDetected -and $exitCode -eq 0))

        $assessment = if ($pass) {
            "Likely success. ExitCode=$exitCode ArtifactDetected=$artifactDetected"
        }
        elseif ($execution.TimedOut) {
            'Attempt timed out.'
        }
        elseif (-not $execution.Started) {
            "Process start failed: $($execution.Error)"
        }
        else {
            "No trustworthy success evidence. ExitCode=$exitCode ArtifactDetected=$artifactDetected"
        }

        $attemptHistory += [pscustomobject]@{
            AttemptNumber = $attemptNumber
            CandidateSource = [string]$candidate.Source
            SelectionReason = [string]$candidate.Reason
            InstallerFingerprint = [string]$fingerprint.Signature
            Command = [string]$command
            Arguments = [string]$candidate.Arguments
            ProcessStarted = [bool]$execution.Started
            TimedOut = [bool]$execution.TimedOut
            Completed = [bool]$execution.Completed
            ExitCode = $exitCode
            RebootRequired = [bool]$rebootRequired
            InstallationArtifactsDetected = [bool]$artifactDetected
            NewUninstallEntriesCount = $newUninstallCount
            NewFilesCount = $newFilesCount
            NewServicesCount = $newServicesCount
            NewShortcutsCount = $newShortcutsCount
            NewProcessesCount = $newProcessesCount
            Pass = [bool]$pass
            Assessment = $assessment
        }

        if ($pass -and $null -eq $winningAttempt) {
            $winningAttempt = $attemptHistory[-1]
            $winningDiff = $diff
            break
        }
    }

    Update-DiscoveryStage -Context $context -Stage 'SilentSwitchAnalysisComplete' -State 'InProgress' -Message "Silent switch analysis completed. Attempts=$($attemptHistory.Count)."

    Update-DiscoveryStage -Context $context -Stage 'DetectionRuleAnalysisStarted' -State 'InProgress' -Message 'Analyzing detection evidence.'
    if ($null -eq $winningDiff) {
        $winningDiff = $lastDiff
    }
    if ($null -eq $winningDiff) {
        $winningDiff = [pscustomobject]@{
            NewUninstallEntries = @()
            NewFiles = @()
            NewRegistryKeys = @()
            NewServices = @()
            NewShortcuts = @()
            NewProcesses = @()
        }
    }

    $primaryDetection = $null
    if ($winningDiff.NewUninstallEntries.Count -gt 0) {
        $primaryDetection = [ordered]@{
            Type = 'RegistryDisplayName'
            Value = [string]$winningDiff.NewUninstallEntries[0]
            Confidence = 'High'
            Reason = 'New uninstall entry detected after install attempt.'
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($appName)) {
        $primaryDetection = [ordered]@{
            Type = 'RegistryDisplayName'
            Value = $appName
            Confidence = 'Medium'
            Reason = 'Fallback to app name; no new uninstall entry found.'
        }
    }

    $secondaryDetection = $null
    $newExePath = $winningDiff.NewFiles | Where-Object { $_ -and $_.ToLowerInvariant().EndsWith('.exe') } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($newExePath)) {
        $secondaryDetection = [ordered]@{
            Type = 'FileExists'
            Value = [string]$newExePath
            Confidence = 'High'
            Reason = 'New executable path detected after install attempt.'
        }
    }
    elseif ($winningDiff.NewRegistryKeys.Count -gt 0) {
        $secondaryDetection = [ordered]@{
            Type = 'RegistryKeyExists'
            Value = [string]$winningDiff.NewRegistryKeys[0]
            Confidence = 'Medium'
            Reason = 'New uninstall registry key detected.'
        }
    }

    Update-DiscoveryStage -Context $context -Stage 'DetectionRuleAnalysisComplete' -State 'InProgress' -Message 'Detection rule analysis completed.'

    $silentSuggestions = @()
    foreach ($candidate in ($candidatePlan | Select-Object -First 10)) {
        $silentSuggestions += [ordered]@{
            Arguments = [string]$candidate.Arguments
            Confidence = Get-ConfidenceLabel -Score ([double]$candidate.BaseConfidence)
            Reason = [string]$candidate.Reason
        }
    }

    $silentRecommendation = [ordered]@{
        RecommendedCommand = ''
        RecommendedArguments = ''
        ConfidenceLabel = 'Low'
        ConfidenceScore = 0.25
        Reason = 'No trustworthy silent install candidate validated. Manual review required.'
        ManualReviewNeeded = $true
    }

    $discoverySuccess = $false
    if ($null -ne $winningAttempt) {
        $discoverySuccess = $true
        $score = 0.55
        if ($winningAttempt.CandidateSource -eq 'MsiStandard') {
            $score = 0.92
        }
        elseif ($winningAttempt.CandidateSource -eq 'HelpProbe') {
            $score = 0.80
        }
        elseif ($winningAttempt.CandidateSource -eq 'Fingerprint') {
            $score = 0.70
        }
        elseif ($winningAttempt.CandidateSource -eq 'PreferredInput') {
            $score = 0.64
        }

        if ($winningAttempt.InstallationArtifactsDetected) {
            $score += 0.12
        }

        if ($winningAttempt.NewUninstallEntriesCount -gt 0) {
            $score += 0.08
        }

        if ($winningAttempt.RebootRequired) {
            $score -= 0.05
        }

        $score = Clamp-Confidence -Score $score
        $confidenceLabel = Get-ConfidenceLabel -Score $score

        $silentRecommendation = [ordered]@{
            RecommendedCommand = [string]$winningAttempt.Command
            RecommendedArguments = [string]$winningAttempt.Arguments
            ConfidenceLabel = $confidenceLabel
            ConfidenceScore = [Math]::Round($score, 2)
            Reason = [string]$winningAttempt.Assessment
            ManualReviewNeeded = ($confidenceLabel -eq 'Low')
        }

        $existing = $silentSuggestions | Where-Object { [string]$_.Arguments -eq [string]$winningAttempt.Arguments } | Select-Object -First 1
        if ($null -eq $existing) {
            $silentSuggestions = @([ordered]@{
                    Arguments = [string]$winningAttempt.Arguments
                    Confidence = $confidenceLabel
                    Reason = 'Validated by discovery attempt evidence.'
                }) + $silentSuggestions
        }
        else {
            $existing.Confidence = $confidenceLabel
            $existing.Reason = 'Validated by discovery attempt evidence.'
            $silentSuggestions = @($existing) + @($silentSuggestions | Where-Object { $_ -ne $existing })
        }
    }

    $errors = @()
    if (-not $discoverySuccess) {
        $errors += 'No silent switch candidate produced trustworthy success evidence. Manual review required.'
    }

    $attemptSummaryLines = @()
    foreach ($attempt in $attemptHistory) {
        $attemptSummaryLines += "Attempt $($attempt.AttemptNumber): [$($attempt.CandidateSource)] $($attempt.Arguments) -> Pass=$($attempt.Pass) Exit=$($attempt.ExitCode) TimedOut=$($attempt.TimedOut) Artifacts=$($attempt.InstallationArtifactsDetected)"
    }

    $result = [ordered]@{
        Success = $discoverySuccess
        InstallerType = $installerType
        SilentSwitchSuggestions = @($silentSuggestions)
        SilentRecommendation = $silentRecommendation
        SilentSwitchAttemptHistory = @($attemptHistory)
        PrimaryDetection = $primaryDetection
        SecondaryDetection = $secondaryDetection
        Evidence = [ordered]@{
            NewUninstallEntries = @($winningDiff.NewUninstallEntries | Select-Object -First 50)
            NewFiles = @($winningDiff.NewFiles | Select-Object -First 120)
            NewRegistryKeys = @($winningDiff.NewRegistryKeys | Select-Object -First 50)
            NewServices = @($winningDiff.NewServices | Select-Object -First 50)
            NewShortcuts = @($winningDiff.NewShortcuts | Select-Object -First 50)
            NewProcesses = @($winningDiff.NewProcesses | Select-Object -First 50)
            ProbeAttempts = @($probeAttempts)
        }
        RawHelpOutput = $rawHelpOutput
        InstallAttemptSummary = ($attemptSummaryLines -join [Environment]::NewLine)
        Errors = @($errors)
    }

    Update-DiscoveryStage -Context $context -Stage 'ResultWriteStart' -State 'InProgress' -Message 'Writing discovery result artifacts.'
    $result | ConvertTo-Json -Depth 10 | Set-Content -Path $context.ResultPath -Encoding UTF8
    Update-DiscoveryStage -Context $context -Stage 'ResultWriteComplete' -State 'InProgress' -Message 'Discovery result JSON written.' -ResultPath $context.ResultPath -LogPath $context.GuestLogPath

    Update-DiscoveryStage -Context $context -Stage 'CompletionSignalStart' -State 'InProgress' -Message 'Publishing completion signal to host.' -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
    Update-DiscoveryStage -Context $context -Stage 'CompletionSignalComplete' -State 'InProgress' -Message 'Completion signal published to host.' -ResultReady $true -Success $discoverySuccess -ResultPath $context.ResultPath -LogPath $context.GuestLogPath

    if ($discoverySuccess) {
        Update-DiscoveryStage -Context $context -Stage 'DiscoverySucceeded' -State 'Completed' -Message 'Discovery completed successfully.' -ResultReady $true -Success $true -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
    }
    else {
        Update-DiscoveryStage -Context $context -Stage 'DiscoveryFailed' -State 'Failed' -Message 'Discovery completed without a trustworthy silent install result.' -Severity 'WARN' -ResultReady $true -Success $false -Error ($errors -join ' | ') -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
    }

    if ($job.ShutdownVmOnComplete -eq $true) {
        Start-Sleep -Seconds 2
        Stop-Computer -Force
    }
}
catch {
    $message = $_.Exception.Message
    if ($null -eq $context) {
        $fallbackJobId = if ($null -ne $job -and -not [string]::IsNullOrWhiteSpace([string]$job.JobId)) {
            [string]$job.JobId
        }
        else {
            "failed_{0}" -f ([guid]::NewGuid().ToString('N'))
        }

        $context = New-DiscoveryJobContext -JobId $fallbackJobId -DiscoveryRoot $discoveryRoot
    }

    $fallbackResult = [ordered]@{
        Success = $false
        InstallerType = 'Unknown'
        SilentSwitchSuggestions = @()
        SilentRecommendation = [ordered]@{
            RecommendedCommand = ''
            RecommendedArguments = ''
            ConfidenceLabel = 'Low'
            ConfidenceScore = 0.0
            Reason = 'Discovery failed before a reliable recommendation could be generated.'
            ManualReviewNeeded = $true
        }
        SilentSwitchAttemptHistory = @()
        PrimaryDetection = $null
        SecondaryDetection = $null
        Evidence = [ordered]@{
            NewUninstallEntries = @()
            NewFiles = @()
            NewRegistryKeys = @()
            NewServices = @()
            NewShortcuts = @()
            NewProcesses = @()
            ProbeAttempts = @()
        }
        RawHelpOutput = ''
        InstallAttemptSummary = ''
        Errors = @($message)
    }

    try {
        Update-DiscoveryStage -Context $context -Stage 'ResultWriteStart' -State 'Failed' -Message 'Writing failure result artifact.' -Severity 'ERROR'
        $fallbackResult | ConvertTo-Json -Depth 10 | Set-Content -Path $context.ResultPath -Encoding UTF8
        Update-DiscoveryStage -Context $context -Stage 'ResultWriteComplete' -State 'Failed' -Message 'Failure result JSON written.' -Severity 'ERROR' -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
        Update-DiscoveryStage -Context $context -Stage 'CompletionSignalStart' -State 'Failed' -Message 'Publishing failure completion signal to host.' -Severity 'ERROR' -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
        Update-DiscoveryStage -Context $context -Stage 'CompletionSignalComplete' -State 'Failed' -Message 'Failure completion signal published.' -Severity 'ERROR' -ResultReady $true -Success $false -Error $message -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
        Update-DiscoveryStage -Context $context -Stage 'DiscoveryFailed' -State 'Failed' -Message $message -Severity 'ERROR' -ResultReady $true -Success $false -Error $message -ResultPath $context.ResultPath -LogPath $context.GuestLogPath
    }
    catch {
        # If status writing also fails, we still rethrow the original exception.
    }

    throw
}
