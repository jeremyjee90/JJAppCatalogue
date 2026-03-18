using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class HyperVDiscoveryService
{
    private readonly FileLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public HyperVDiscoveryService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<HyperVDiscoveryRunResult> RunDiscoveryAsync(
        HyperVDiscoveryRequest request,
        IProgress<DiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var settings = request.Settings;
        EnsureDiscoveryDirectories(settings);

        var errors = new List<string>();
        var staged = StageInputs(request, settings);
        var jobLogger = new DiscoveryJobLogger(staged.JobId, staged.HostResultDirectory, _logger);
        var guestArtifacts = new CollectedGuestArtifacts(
            staged.HostGuestArtifactsDirectory,
            staged.GuestLogPath,
            staged.GuestStatusPath,
            staged.RawResultPath);
        var cleanupNeeded = false;
        var finalStage = "JobCreated";
        var currentStage = "JobCreated";

        Report(progress, DiscoveryProgressStage.PreparingLab, $"Discovery start for '{request.AppName}'.");
        jobLogger.LogInfo(
            "JobCreated",
            $"Discovery start. App='{request.AppName}', Installer='{request.InstallerPath}', VM='{settings.VmName}', Checkpoint='{settings.CheckpointName}'.");
        jobLogger.UpdateStatus(
            state: "InProgress",
            stage: "JobCreated",
            message: "Discovery job created on host.",
            success: null,
            resultPath: staged.RawResultPath,
            logPath: jobLogger.HostLogPath);

        try
        {
            await EnsureVmAndCheckpointExistAsync(settings, cancellationToken);
            cleanupNeeded = true;

            currentStage = "CheckpointRestoreStart";
            jobLogger.LogInfo(currentStage, $"Restoring checkpoint '{settings.CheckpointName}'.");
            jobLogger.UpdateStatus("InProgress", currentStage, $"Restoring checkpoint '{settings.CheckpointName}'.");
            Report(progress, DiscoveryProgressStage.RestoringCheckpoint, $"Restoring checkpoint '{settings.CheckpointName}'.");
            await RestoreCheckpointAsync(settings, cancellationToken);
            jobLogger.LogInfo("CheckpointRestoreComplete", $"Checkpoint '{settings.CheckpointName}' restored.");
            jobLogger.UpdateStatus("InProgress", "CheckpointRestoreComplete", $"Checkpoint '{settings.CheckpointName}' restored.");

            Report(progress, DiscoveryProgressStage.StartingVm, $"Starting VM '{settings.VmName}'.");
            await StartVmAsync(settings, cancellationToken);

            Report(progress, DiscoveryProgressStage.WaitingForGuest, "Waiting for guest heartbeat.");
            await WaitForGuestReadyAsync(settings, progress, cancellationToken);

            currentStage = "InstallerCopyStart";
            jobLogger.LogInfo(currentStage, "Copying discovery scripts and installer into guest.");
            jobLogger.UpdateStatus("InProgress", currentStage, "Copying discovery scripts and installer into guest.");
            Report(progress, DiscoveryProgressStage.CopyingInstaller, "Copying discovery scripts and installer into guest.");
            await CopyInputFilesToGuestAsync(request, settings, staged, cancellationToken);
            jobLogger.LogInfo("InstallerCopyComplete", "Discovery scripts and installer copied into guest.");
            jobLogger.UpdateStatus("InProgress", "InstallerCopyComplete", "Discovery scripts and installer copied into guest.");

            currentStage = "GuestBootstrapStart";
            jobLogger.LogInfo(currentStage, "Submitting discovery job to guest watcher.");
            jobLogger.UpdateStatus("InProgress", currentStage, "Submitting discovery job to guest watcher.");
            Report(progress, DiscoveryProgressStage.SubmittingDiscoveryJob, "Submitting file-based discovery job to guest watcher.");
            await SubmitDiscoveryJobAsync(settings, staged, cancellationToken);
            jobLogger.LogInfo("GuestBootstrapComplete", "Discovery job files submitted to guest watcher.");
            jobLogger.UpdateStatus("InProgress", "GuestBootstrapComplete", "Discovery job files submitted to guest watcher.");

            currentStage = "WaitForGuestSignalStart";
            jobLogger.LogInfo(currentStage, "Waiting for guest completion signal.");
            jobLogger.UpdateStatus("InProgress", currentStage, "Waiting for guest completion signal.");
            Report(progress, DiscoveryProgressStage.WaitingForGuestResults, "Waiting for guest discovery completion.");
            await WaitForGuestCompletionAsync(
                settings,
                staged.JobId,
                progress,
                signal =>
                {
                    if (string.IsNullOrWhiteSpace(signal.Stage))
                    {
                        return;
                    }

                    currentStage = signal.Stage;
                    if (signal.Stage.Equals("DiscoverySucceeded", StringComparison.OrdinalIgnoreCase) ||
                        signal.Stage.Equals("DiscoveryFailed", StringComparison.OrdinalIgnoreCase))
                    {
                        finalStage = signal.Stage;
                    }

                    var guestMessage = string.IsNullOrWhiteSpace(signal.Message)
                        ? signal.Stage
                        : $"{signal.Stage}: {signal.Message}";
                    jobLogger.LogInfo("GuestSignalReceived", guestMessage);
                    jobLogger.UpdateStatus(
                        string.IsNullOrWhiteSpace(signal.State) ? "InProgress" : signal.State,
                        "GuestSignalReceived",
                        guestMessage,
                        success: signal.Success,
                        error: signal.Error,
                        resultPath: signal.ResultPath,
                        logPath: jobLogger.HostLogPath);
                },
                cancellationToken);

            currentStage = "ResultCollectionStart";
            jobLogger.LogInfo(currentStage, "Collecting guest artifacts from VM disk.");
            jobLogger.UpdateStatus("InProgress", currentStage, "Collecting guest artifacts from VM disk.");
            Report(progress, DiscoveryProgressStage.CollectingResults, "Collecting discovery output from VM disk.");
            await CollectGuestOutputAsync(settings, staged, cancellationToken);
            guestArtifacts = ResolveCollectedGuestArtifacts(staged);
            await CopyArtifactsToLegacyDirectoryAsync(staged, guestArtifacts, cancellationToken);
            jobLogger.LogInfo("ResultCollectionComplete", $"Guest artifacts collected to '{guestArtifacts.ArtifactsDirectory}'.");
            jobLogger.UpdateStatus(
                "InProgress",
                "ResultCollectionComplete",
                "Guest artifacts collected.",
                resultPath: guestArtifacts.ResultPath,
                logPath: jobLogger.HostLogPath);

            currentStage = "ResultParseStart";
            jobLogger.LogInfo(currentStage, "Parsing discovery result and guest status files.");
            jobLogger.UpdateStatus(
                "InProgress",
                currentStage,
                "Parsing discovery result and guest status files.",
                resultPath: guestArtifacts.ResultPath);
            var discoveryResult = await LoadDiscoveryResultAsync(guestArtifacts.ResultPath, cancellationToken);
            var discoveryStatus = await LoadDiscoveryStatusAsync(guestArtifacts.StatusPath, cancellationToken);
            if (!discoveryResult.Success)
            {
                foreach (var error in discoveryResult.Errors)
                {
                    errors.Add(error);
                }
            }

            if (discoveryStatus is not null && !string.IsNullOrWhiteSpace(discoveryStatus.Error))
            {
                errors.Add(discoveryStatus.Error);
            }

            jobLogger.LogInfo("ResultParseComplete", "Discovery result parsing complete.");
            jobLogger.UpdateStatus(
                "InProgress",
                "ResultParseComplete",
                "Discovery result parsing complete.",
                success: discoveryResult.Success,
                error: string.Join(" | ", errors.Where(e => !string.IsNullOrWhiteSpace(e))),
                resultPath: guestArtifacts.ResultPath,
                logPath: jobLogger.HostLogPath);

            var summary = discoveryResult.Success
                ? "Discovery completed successfully."
                : "Discovery completed with warnings. Review errors/evidence.";

            Report(progress, DiscoveryProgressStage.Complete, summary);
            finalStage = "DiscoveryCompleted";
            jobLogger.LogInfo(finalStage, $"Discovery completed. Success={discoveryResult.Success}, ResultPath='{guestArtifacts.ResultPath}'.");
            jobLogger.UpdateStatus(
                discoveryResult.Success ? "Completed" : "CompletedWithWarnings",
                finalStage,
                summary,
                success: discoveryResult.Success,
                error: string.Join(" | ", errors.Where(e => !string.IsNullOrWhiteSpace(e))),
                resultPath: guestArtifacts.ResultPath,
                logPath: jobLogger.HostLogPath);

            return new HyperVDiscoveryRunResult
            {
                Success = discoveryResult.Success,
                Summary = summary,
                HostResultDirectory = staged.HostResultDirectory,
                HostJobDirectory = staged.HostResultDirectory,
                HostLogPath = jobLogger.HostLogPath,
                HostStatusPath = jobLogger.StatusPath,
                GuestArtifactsDirectory = guestArtifacts.ArtifactsDirectory,
                GuestLogPath = guestArtifacts.LogPath,
                GuestStatusPath = guestArtifacts.StatusPath,
                GuestResultPath = guestArtifacts.ResultPath,
                RawResultPath = guestArtifacts.ResultPath,
                CurrentStage = currentStage,
                FinalStage = finalStage,
                Status = discoveryStatus ?? jobLogger.CurrentStatus,
                DiscoveryResult = discoveryResult,
                Errors = errors
            };
        }
        catch (TimeoutException ex)
        {
            finalStage = "Timeout";
            currentStage = finalStage;
            var message = $"Discovery timed out: {ex.Message}";
            errors.Add(message);
            jobLogger.LogError(finalStage, message, ex);
            jobLogger.UpdateStatus(
                "Timeout",
                finalStage,
                message,
                success: false,
                error: ex.Message,
                resultPath: guestArtifacts.ResultPath,
                logPath: jobLogger.HostLogPath);
            Report(progress, DiscoveryProgressStage.Failed, message, isError: true);

            guestArtifacts = await TryCollectGuestArtifactsOnFailureAsync(
                settings,
                staged,
                jobLogger,
                "timeout",
                cancellationToken);

            var discoveryStatus = await LoadDiscoveryStatusAsync(guestArtifacts.StatusPath, cancellationToken);
            var discoveryResult = await LoadDiscoveryResultAsync(guestArtifacts.ResultPath, cancellationToken);
            AppendDiscoveryErrors(errors, discoveryStatus, discoveryResult);

            return new HyperVDiscoveryRunResult
            {
                Success = false,
                Summary = message,
                HostResultDirectory = staged.HostResultDirectory,
                HostJobDirectory = staged.HostResultDirectory,
                HostLogPath = jobLogger.HostLogPath,
                HostStatusPath = jobLogger.StatusPath,
                GuestArtifactsDirectory = guestArtifacts.ArtifactsDirectory,
                GuestLogPath = guestArtifacts.LogPath,
                GuestStatusPath = guestArtifacts.StatusPath,
                GuestResultPath = guestArtifacts.ResultPath,
                RawResultPath = guestArtifacts.ResultPath,
                CurrentStage = currentStage,
                FinalStage = finalStage,
                Status = discoveryStatus ?? jobLogger.CurrentStatus,
                DiscoveryResult = discoveryResult,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            finalStage = "DiscoveryFailed";
            currentStage = finalStage;
            var message = $"Discovery failed: {ex.Message}";
            errors.Add(message);
            jobLogger.LogError(finalStage, message, ex);
            jobLogger.UpdateStatus(
                "Failed",
                finalStage,
                message,
                success: false,
                error: ex.Message,
                resultPath: guestArtifacts.ResultPath,
                logPath: jobLogger.HostLogPath);
            Report(progress, DiscoveryProgressStage.Failed, message, isError: true);

            guestArtifacts = await TryCollectGuestArtifactsOnFailureAsync(
                settings,
                staged,
                jobLogger,
                "failure",
                cancellationToken);

            var discoveryStatus = await LoadDiscoveryStatusAsync(guestArtifacts.StatusPath, cancellationToken);
            var discoveryResult = await LoadDiscoveryResultAsync(guestArtifacts.ResultPath, cancellationToken);
            AppendDiscoveryErrors(errors, discoveryStatus, discoveryResult);

            return new HyperVDiscoveryRunResult
            {
                Success = false,
                Summary = message,
                HostResultDirectory = staged.HostResultDirectory,
                HostJobDirectory = staged.HostResultDirectory,
                HostLogPath = jobLogger.HostLogPath,
                HostStatusPath = jobLogger.StatusPath,
                GuestArtifactsDirectory = guestArtifacts.ArtifactsDirectory,
                GuestLogPath = guestArtifacts.LogPath,
                GuestStatusPath = guestArtifacts.StatusPath,
                GuestResultPath = guestArtifacts.ResultPath,
                RawResultPath = guestArtifacts.ResultPath,
                CurrentStage = currentStage,
                FinalStage = finalStage,
                Status = discoveryStatus ?? jobLogger.CurrentStatus,
                DiscoveryResult = discoveryResult,
                Errors = errors
            };
        }
        finally
        {
            if (cleanupNeeded)
            {
                await AttemptVmCleanupAsync(settings, progress, cancellationToken);
            }
        }
    }

    private static void ValidateRequest(HyperVDiscoveryRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.AppName))
        {
            throw new InvalidOperationException("App name is required for discovery.");
        }

        if (string.IsNullOrWhiteSpace(request.InstallerPath))
        {
            throw new InvalidOperationException("Installer path is required for discovery.");
        }

        if (!File.Exists(request.InstallerPath))
        {
            throw new FileNotFoundException("Installer file was not found.", request.InstallerPath);
        }

        if (string.IsNullOrWhiteSpace(request.GuestScriptSourceDirectory))
        {
            throw new InvalidOperationException("Guest script source directory is required.");
        }
    }

    private static void EnsureDiscoveryDirectories(DiscoveryModeSettings settings)
    {
        Directory.CreateDirectory(settings.HostStagingDirectory);
        Directory.CreateDirectory(settings.HostResultsDirectory);
        Directory.CreateDirectory(AppPaths.EndpointDiscoveryJobsDirectory);
    }

    private static StagedDiscoveryArtifacts StageInputs(HyperVDiscoveryRequest request, DiscoveryModeSettings settings)
    {
        var jobId = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        var hostStagingJobDirectory = Path.Combine(settings.HostStagingDirectory, jobId);
        var hostResultDirectory = Path.Combine(AppPaths.EndpointDiscoveryJobsDirectory, jobId);
        var hostGuestArtifactsDirectory = Path.Combine(hostResultDirectory, "guest-artifacts");
        var legacyResultDirectory = Path.Combine(settings.HostResultsDirectory, jobId);

        Directory.CreateDirectory(hostStagingJobDirectory);
        Directory.CreateDirectory(hostResultDirectory);
        Directory.CreateDirectory(hostGuestArtifactsDirectory);
        Directory.CreateDirectory(legacyResultDirectory);

        var extension = Path.GetExtension(request.InstallerPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".exe";
        }

        var stagedInstallerPath = Path.Combine(hostStagingJobDirectory, $"installer{extension}");
        File.Copy(request.InstallerPath, stagedInstallerPath, overwrite: true);

        var job = new HyperVDiscoveryJob
        {
            JobId = jobId,
            AppName = request.AppName.Trim(),
            InstallerFileName = Path.GetFileName(stagedInstallerPath),
            PreferredSilentArguments = request.PreferredSilentArguments?.Trim() ?? string.Empty,
            ProbeTimeoutSeconds = settings.ProbeTimeoutSeconds,
            InstallerTimeoutSeconds = settings.InstallerTimeoutSeconds,
            ShutdownVmOnComplete = settings.ShutdownVmOnComplete,
            SubmittedUtc = DateTime.UtcNow
        };

        var jobPath = Path.Combine(hostStagingJobDirectory, "job.json");
        var jobJson = JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jobPath, jobJson, Encoding.UTF8);

        var triggerPath = Path.Combine(hostStagingJobDirectory, "run.trigger");
        File.WriteAllText(triggerPath, DateTime.UtcNow.ToString("O"), Encoding.UTF8);

        return new StagedDiscoveryArtifacts(
            jobId,
            hostStagingJobDirectory,
            stagedInstallerPath,
            jobPath,
            triggerPath,
            hostResultDirectory,
            hostGuestArtifactsDirectory,
            legacyResultDirectory,
            Path.Combine(hostGuestArtifactsDirectory, "Jobs", jobId, "discovery-results.json"),
            Path.Combine(hostGuestArtifactsDirectory, "Jobs", jobId, "discovery-status.json"),
            Path.Combine(hostGuestArtifactsDirectory, "Jobs", jobId, "guest.log"));
    }

    private async Task EnsureVmAndCheckpointExistAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vmName = '__VM_NAME__'
        $checkpointName = '__CHECKPOINT_NAME__'
        try {
            $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
        }
        catch {
            throw "Unable to query Hyper-V VM '$vmName'. Ensure AppCatalogueAdmin is elevated and your account has Hyper-V permissions. $($_.Exception.Message)"
        }

        if ($null -eq $vm) {
            $availableVms = @()
            try {
                $availableVms = Get-VM | Select-Object -ExpandProperty Name
            }
            catch {
                # ignore list failure and return the primary not-found error
            }

            if ($availableVms.Count -gt 0) {
                throw "VM not found: $vmName. Available VMs: $($availableVms -join ', ')"
            }

            throw "VM not found: $vmName"
        }

        try {
            $checkpoint = Get-VMCheckpoint -VMName $vmName -Name $checkpointName -ErrorAction SilentlyContinue
        }
        catch {
            throw "Unable to query checkpoint '$checkpointName' for VM '$vmName'. $($_.Exception.Message)"
        }

        if ($null -eq $checkpoint) {
            throw "Checkpoint not found: $checkpointName"
        }
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal)
        .Replace("__CHECKPOINT_NAME__", EscapeForSingleQuotedPowerShell(settings.CheckpointName), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            settings.CommandTimeoutSeconds,
            "Hyper-V precheck",
            cancellationToken);
    }

    private async Task RestoreCheckpointAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        Restore-VMCheckpoint -VMName '__VM_NAME__' -Name '__CHECKPOINT_NAME__' -Confirm:$false -ErrorAction Stop
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal)
        .Replace("__CHECKPOINT_NAME__", EscapeForSingleQuotedPowerShell(settings.CheckpointName), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            settings.CommandTimeoutSeconds,
            "Restore-VMCheckpoint",
            cancellationToken);
    }

    private async Task StartVmAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vm = Get-VM -Name '__VM_NAME__' -ErrorAction Stop
        if ($vm.State -ne 'Running') {
            Start-VM -Name '__VM_NAME__' -ErrorAction Stop | Out-Null
        }
        Enable-VMIntegrationService -VMName '__VM_NAME__' -Name 'Guest Service Interface' -ErrorAction SilentlyContinue | Out-Null
        Enable-VMIntegrationService -VMName '__VM_NAME__' -Name 'Key-Value Pair Exchange' -ErrorAction SilentlyContinue | Out-Null
        Enable-VMIntegrationService -VMName '__VM_NAME__' -Name 'Heartbeat' -ErrorAction SilentlyContinue | Out-Null
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            settings.CommandTimeoutSeconds,
            "Start-VM",
            cancellationToken);
    }

    private async Task WaitForGuestReadyAsync(
        DiscoveryModeSettings settings,
        IProgress<DiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(settings.GuestReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var heartbeatOutput = await GetHeartbeatStatusAsync(settings, cancellationToken);
            if (heartbeatOutput.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log($"Guest heartbeat is ready for VM '{settings.VmName}'.");
                return;
            }

            Report(progress, DiscoveryProgressStage.WaitingForGuest, $"Heartbeat status: {heartbeatOutput}");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new TimeoutException(
            $"VM '{settings.VmName}' did not report a ready heartbeat within {settings.GuestReadyTimeoutSeconds} seconds.");
    }

    private async Task CopyInputFilesToGuestAsync(
        HyperVDiscoveryRequest request,
        DiscoveryModeSettings settings,
        StagedDiscoveryArtifacts staged,
        CancellationToken cancellationToken)
    {
        var runScript = Path.Combine(request.GuestScriptSourceDirectory, "Run-Discovery.ps1");
        var watcherScript = Path.Combine(request.GuestScriptSourceDirectory, "Discovery-Watcher.ps1");
        var bootstrapScript = Path.Combine(request.GuestScriptSourceDirectory, "Install-DiscoveryBootstrap.ps1");
        var loggingScript = Path.Combine(request.GuestScriptSourceDirectory, "Discovery-Logging.ps1");

        if (!File.Exists(runScript) ||
            !File.Exists(watcherScript) ||
            !File.Exists(bootstrapScript) ||
            !File.Exists(loggingScript))
        {
            throw new InvalidOperationException(
                $"Guest script package is missing. Expected scripts under '{request.GuestScriptSourceDirectory}'.");
        }

        var guestScriptsDirectory = NormalizeWindowsDirectory(settings.GuestScriptsDirectory);
        var guestInputDirectory = NormalizeWindowsDirectory(settings.GuestInputDirectory);

        await CopyFileToGuestAsync(settings, runScript, CombineWindowsPath(guestScriptsDirectory, "Run-Discovery.ps1"), cancellationToken);
        await CopyFileToGuestAsync(settings, watcherScript, CombineWindowsPath(guestScriptsDirectory, "Discovery-Watcher.ps1"), cancellationToken);
        await CopyFileToGuestAsync(settings, bootstrapScript, CombineWindowsPath(guestScriptsDirectory, "Install-DiscoveryBootstrap.ps1"), cancellationToken);
        await CopyFileToGuestAsync(settings, loggingScript, CombineWindowsPath(guestScriptsDirectory, "Discovery-Logging.ps1"), cancellationToken);

        await CopyFileToGuestAsync(
            settings,
            staged.StagedInstallerPath,
            CombineWindowsPath(guestInputDirectory, Path.GetFileName(staged.StagedInstallerPath)),
            cancellationToken);
    }

    private async Task SubmitDiscoveryJobAsync(
        DiscoveryModeSettings settings,
        StagedDiscoveryArtifacts staged,
        CancellationToken cancellationToken)
    {
        var guestInputDirectory = NormalizeWindowsDirectory(settings.GuestInputDirectory);

        await CopyFileToGuestAsync(
            settings,
            staged.JobPath,
            CombineWindowsPath(guestInputDirectory, "job.json"),
            cancellationToken);

        await CopyFileToGuestAsync(
            settings,
            staged.TriggerPath,
            CombineWindowsPath(guestInputDirectory, "run.trigger"),
            cancellationToken);
    }

    private async Task WaitForGuestCompletionAsync(
        DiscoveryModeSettings settings,
        string jobId,
        IProgress<DiscoveryProgressUpdate>? progress,
        Action<GuestDiscoverySignal>? onSignal,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(settings.DiscoveryTimeoutSeconds);
        var waitStartedUtc = DateTime.UtcNow;
        var noSignalTimeout = TimeSpan.FromSeconds(180);
        var watcherNoPickupTimeout = TimeSpan.FromSeconds(180);
        var stopTriggeredBySignal = false;
        var lastGuestSignalFingerprint = string.Empty;
        var lastGuestStatusMessage = string.Empty;
        var firstSignalUtc = (DateTime?)null;
        var firstWatcherReadyUtc = (DateTime?)null;
        var firstJobMatchedUtc = (DateTime?)null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vmState = await GetVmStateAsync(settings, cancellationToken);
            if (vmState.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log($"VM '{settings.VmName}' powered off after discovery execution.");
                return;
            }

            var guestSignal = await GetGuestSignalAsync(settings, cancellationToken);
            if (guestSignal is not null && !string.IsNullOrWhiteSpace(guestSignal.Stage))
            {
                firstSignalUtc ??= DateTime.UtcNow;

                if (guestSignal.Stage.Equals("WatcherReady", StringComparison.OrdinalIgnoreCase) ||
                    guestSignal.Stage.Equals("WatcherStarted", StringComparison.OrdinalIgnoreCase))
                {
                    firstWatcherReadyUtc ??= DateTime.UtcNow;
                }

                var isMatchingJobSignal =
                    !string.IsNullOrWhiteSpace(guestSignal.JobId) &&
                    guestSignal.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase);

                if (isMatchingJobSignal)
                {
                    firstJobMatchedUtc ??= DateTime.UtcNow;
                }

                lastGuestStatusMessage = $"{guestSignal.Stage}: {guestSignal.Message}";
                var fingerprint = $"{guestSignal.Stage}|{guestSignal.Message}|{guestSignal.ResultReady}";
                if (!string.Equals(fingerprint, lastGuestSignalFingerprint, StringComparison.Ordinal))
                {
                    lastGuestSignalFingerprint = fingerprint;
                    onSignal?.Invoke(guestSignal);
                    Report(
                        progress,
                        DiscoveryProgressStage.WaitingForGuestResults,
                        $"Guest status: {guestSignal.Stage} - {guestSignal.Message}");
                }

                var stageIsComplete =
                    guestSignal.Stage.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                    guestSignal.Stage.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                    guestSignal.Stage.Equals("DiscoverySucceeded", StringComparison.OrdinalIgnoreCase) ||
                    guestSignal.Stage.Equals("DiscoveryFailed", StringComparison.OrdinalIgnoreCase);

                var terminalSignalForCurrentRun =
                    isMatchingJobSignal ||
                    string.IsNullOrWhiteSpace(guestSignal.JobId);

                if (terminalSignalForCurrentRun && guestSignal.ResultReady && stageIsComplete && !stopTriggeredBySignal)
                {
                    stopTriggeredBySignal = true;
                    Report(
                        progress,
                        DiscoveryProgressStage.WaitingForGuestResults,
                        "Guest signaled result-ready. Stopping VM for result collection.");

                    try
                    {
                        await StopVmIfRunningAsync(settings, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"Guest completion stop signal failed: {ex.Message}");
                    }
                }

                if (!isMatchingJobSignal &&
                    firstWatcherReadyUtc.HasValue &&
                    !firstJobMatchedUtc.HasValue &&
                    DateTime.UtcNow - firstWatcherReadyUtc.Value > watcherNoPickupTimeout)
                {
                    throw new TimeoutException(
                        $"Guest watcher is running but did not pick up job '{jobId}' within {watcherNoPickupTimeout.TotalSeconds} seconds. " +
                        "Verify C:\\Discovery\\Input\\job.json is being processed and recreate CleanState after running Install-DiscoveryBootstrap.ps1.");
                }
            }
            else
            {
                Report(progress, DiscoveryProgressStage.WaitingForGuestResults, $"Guest processing in progress (VM state: {vmState}).");

                if (DateTime.UtcNow - waitStartedUtc > noSignalTimeout)
                {
                    throw new TimeoutException(
                        $"No guest discovery status signal was received within {noSignalTimeout.TotalSeconds} seconds. " +
                        "This usually means the VM checkpoint does not have the discovery watcher bootstrap active. " +
                        "In VM: run C:\\Discovery\\Scripts\\Install-DiscoveryBootstrap.ps1, confirm task AppCatalogueDiscoveryWatcher is running, then recreate checkpoint CleanState.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        var timeoutMessage =
            $"Timed out waiting for discovery results from VM '{settings.VmName}'. " +
            "Ensure guest bootstrap is installed and watcher task is running.";

        if (!string.IsNullOrWhiteSpace(lastGuestStatusMessage))
        {
            timeoutMessage += $" Last guest status: {lastGuestStatusMessage}";
        }

        throw new TimeoutException(timeoutMessage);
    }

    private async Task<GuestDiscoverySignal?> GetGuestSignalAsync(
        DiscoveryModeSettings settings,
        CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop

        $vm = Get-VM -Name '__VM_NAME__' -ErrorAction Stop
        $vmId = $vm.VMId.Guid
        $computer = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "Name='$vmId'"
        if ($null -eq $computer) {
            return
        }

        $kvpComponent = Get-CimAssociatedInstance -InputObject $computer -ResultClassName Msvm_KvpExchangeComponent | Select-Object -First 1
        if ($null -eq $kvpComponent) {
            return
        }

        $allItems = @()
        if ($kvpComponent.GuestExchangeItems) {
            $allItems += $kvpComponent.GuestExchangeItems
        }
        if ($kvpComponent.GuestIntrinsicExchangeItems) {
            $allItems += $kvpComponent.GuestIntrinsicExchangeItems
        }

        $map = @{}
        foreach ($item in $allItems) {
            try {
                $xml = [xml]$item
                $nameNode = $xml.INSTANCE.PROPERTY | Where-Object { $_.Name -eq 'Name' } | Select-Object -First 1
                $valueNode = $xml.INSTANCE.PROPERTY | Where-Object { $_.Name -eq 'Data' } | Select-Object -First 1
                if ($null -ne $nameNode -and $null -ne $valueNode) {
                    $map[[string]$nameNode.VALUE] = [string]$valueNode.VALUE
                }
            }
            catch {
                # best effort parse only
            }
        }

        $successRaw = $map['AppCatalogueDiscoverySuccess']
        $successValue = $null
        if ($successRaw -eq 'true') {
            $successValue = $true
        }
        elseif ($successRaw -eq 'false') {
            $successValue = $false
        }

        $payload = [ordered]@{
            Stage = $map['AppCatalogueDiscoveryStage']
            Message = $map['AppCatalogueDiscoveryMessage']
            State = $map['AppCatalogueDiscoveryState']
            ResultReady = (($map['AppCatalogueDiscoveryResultReady']) -eq 'true')
            JobId = $map['AppCatalogueDiscoveryJobId']
            Success = $successValue
            Error = $map['AppCatalogueDiscoveryError']
            ResultPath = $map['AppCatalogueDiscoveryResultPath']
            LogPath = $map['AppCatalogueDiscoveryLogPath']
            UpdatedUtc = $map['AppCatalogueDiscoveryUpdatedUtc']
        }
        $payload | ConvertTo-Json -Compress
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal);

        var result = await RunPowerShellAsync(script, settings.CommandTimeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var jsonLine = result.StandardOutput
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            var signal = JsonSerializer.Deserialize<GuestDiscoverySignal>(jsonLine, _jsonOptions);
            return signal;
        }
        catch
        {
            return null;
        }
    }

    private async Task CollectGuestOutputAsync(
        DiscoveryModeSettings settings,
        StagedDiscoveryArtifacts staged,
        CancellationToken cancellationToken)
    {
        var guestOutputRelativePath = ToPathInsideOsVolume(settings.GuestOutputDirectory);

        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vmName = '__VM_NAME__'
        $destination = '__DESTINATION__'
        $guestOutputRelative = '__GUEST_OUTPUT__'
        $diskPath = (Get-VMHardDiskDrive -VMName $vmName | Select-Object -First 1).Path
        if ([string]::IsNullOrWhiteSpace($diskPath)) {
            throw "No VM disk path found for VM '$vmName'."
        }

        $mount = Mount-VHD -Path $diskPath -ReadOnly -PassThru -ErrorAction Stop
        try {
            $disk = $mount | Get-Disk
            $volume = Get-Partition -DiskNumber $disk.Number | Get-Volume | Sort-Object -Property Size -Descending | Select-Object -First 1
            if ($null -eq $volume) {
                throw "Unable to locate mounted volume for discovery results."
            }

            $rootPath = $volume.Path
            if ([string]::IsNullOrWhiteSpace($rootPath)) {
                throw "Mounted volume path is unavailable."
            }

            $sourcePath = Join-Path $rootPath $guestOutputRelative
            if (-not (Test-Path $sourcePath)) {
                throw "Guest output path not found: $sourcePath"
            }

            New-Item -Path $destination -ItemType Directory -Force | Out-Null
            Copy-Item -Path (Join-Path $sourcePath '*') -Destination $destination -Recurse -Force
        }
        finally {
            Dismount-VHD -Path $diskPath -ErrorAction SilentlyContinue
        }
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal)
        .Replace("__DESTINATION__", EscapeForSingleQuotedPowerShell(staged.HostGuestArtifactsDirectory), StringComparison.Ordinal)
        .Replace("__GUEST_OUTPUT__", EscapeForSingleQuotedPowerShell(guestOutputRelativePath), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            Math.Max(settings.CommandTimeoutSeconds, 180),
            "Collect guest output",
            cancellationToken);
    }

    private async Task<CollectedGuestArtifacts> TryCollectGuestArtifactsOnFailureAsync(
        DiscoveryModeSettings settings,
        StagedDiscoveryArtifacts staged,
        DiscoveryJobLogger jobLogger,
        string failureMode,
        CancellationToken cancellationToken)
    {
        try
        {
            jobLogger.LogInfo("ResultCollectionStart", $"Attempting guest artifact collection after {failureMode}.");
            jobLogger.UpdateStatus("InProgress", "ResultCollectionStart", $"Attempting guest artifact collection after {failureMode}.");

            try
            {
                await StopVmIfRunningAsync(settings, cancellationToken);
            }
            catch (Exception stopEx)
            {
                jobLogger.LogWarning("ResultCollectionStart", $"Best-effort Stop-VM before artifact collection failed: {stopEx.Message}");
            }

            await CollectGuestOutputAsync(settings, staged, cancellationToken);
            var collected = ResolveCollectedGuestArtifacts(staged);
            await CopyArtifactsToLegacyDirectoryAsync(staged, collected, cancellationToken);

            jobLogger.LogInfo("ResultCollectionComplete", $"Guest artifacts collected to '{collected.ArtifactsDirectory}'.");
            jobLogger.UpdateStatus(
                "InProgress",
                "ResultCollectionComplete",
                "Guest artifacts collected.",
                resultPath: collected.ResultPath,
                logPath: jobLogger.HostLogPath);

            return collected;
        }
        catch (Exception ex)
        {
            jobLogger.LogWarning("ResultCollectionComplete", $"Guest artifact collection was not successful: {ex.Message}");
            return ResolveCollectedGuestArtifacts(staged);
        }
    }

    private static CollectedGuestArtifacts ResolveCollectedGuestArtifacts(StagedDiscoveryArtifacts staged)
    {
        var artifactsDirectory = staged.HostGuestArtifactsDirectory;
        if (!Directory.Exists(artifactsDirectory))
        {
            return new CollectedGuestArtifacts(
                artifactsDirectory,
                staged.GuestLogPath,
                staged.GuestStatusPath,
                staged.RawResultPath);
        }

        var logPath = ResolveArtifactPath(
            artifactsDirectory,
            staged.GuestLogPath,
            "guest.log",
            "Run-Discovery.log");
        var statusPath = ResolveArtifactPath(
            artifactsDirectory,
            staged.GuestStatusPath,
            "discovery-status.json",
            "status.json");
        var resultPath = ResolveArtifactPath(
            artifactsDirectory,
            staged.RawResultPath,
            "discovery-results.json");

        return new CollectedGuestArtifacts(artifactsDirectory, logPath, statusPath, resultPath);
    }

    private static string ResolveArtifactPath(
        string artifactDirectory,
        string preferredPath,
        params string[] fileNames)
    {
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        foreach (var fileName in fileNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var rootCandidate = Path.Combine(artifactDirectory, fileName);
            if (File.Exists(rootCandidate))
            {
                return rootCandidate;
            }

            var match = Directory.EnumerateFiles(artifactDirectory, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return preferredPath;
    }

    private static Task CopyArtifactsToLegacyDirectoryAsync(
        StagedDiscoveryArtifacts staged,
        CollectedGuestArtifacts collected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(staged.LegacyResultDirectory);

        if (!Directory.Exists(collected.ArtifactsDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(collected.ArtifactsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(collected.ArtifactsDirectory, sourcePath);
            var destinationPath = Path.Combine(staged.LegacyResultDirectory, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private async Task<DiscoveryStatusContract?> LoadDiscoveryStatusAsync(string statusPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(statusPath, Encoding.UTF8, cancellationToken);
            var status = JsonSerializer.Deserialize<DiscoveryStatusContract>(json, _jsonOptions);
            return status;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendDiscoveryErrors(
        List<string> errors,
        DiscoveryStatusContract? status,
        HyperVDiscoveryResult? result)
    {
        void AddIfPresent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!errors.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(value);
            }
        }

        if (status is not null)
        {
            AddIfPresent(status.Error);
            if (status.Success == false &&
                !string.IsNullOrWhiteSpace(status.Message) &&
                !status.Message.Equals(status.Error, StringComparison.OrdinalIgnoreCase))
            {
                AddIfPresent(status.Message);
            }
        }

        if (result?.Errors is not null)
        {
            foreach (var error in result.Errors)
            {
                AddIfPresent(error);
            }
        }
    }

    private async Task<HyperVDiscoveryResult> LoadDiscoveryResultAsync(string resultPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(resultPath))
        {
            return new HyperVDiscoveryResult
            {
                Success = false,
                Errors =
                [
                    $"Discovery result file not found at '{resultPath}'.",
                    "This commonly means guest bootstrap/watcher is not installed or the installer never completed."
                ]
            };
        }

        var json = await File.ReadAllTextAsync(resultPath, Encoding.UTF8, cancellationToken);
        var parsed = JsonSerializer.Deserialize<HyperVDiscoveryResult>(json, _jsonOptions);
        if (parsed is null)
        {
            return new HyperVDiscoveryResult
            {
                Success = false,
                Errors = [$"Failed to parse discovery result JSON at '{resultPath}'."]
            };
        }

        parsed.SilentSwitchSuggestions ??= [];
        parsed.SilentRecommendation ??= new SilentSwitchRecommendation();
        parsed.SilentSwitchAttemptHistory ??= [];
        parsed.Evidence ??= new DiscoveryEvidence();
        parsed.Errors ??= [];
        parsed.Evidence.NewUninstallEntries ??= [];
        parsed.Evidence.NewFiles ??= [];
        parsed.Evidence.NewRegistryKeys ??= [];
        parsed.Evidence.NewServices ??= [];
        parsed.Evidence.NewShortcuts ??= [];
        parsed.Evidence.NewProcesses ??= [];
        parsed.Evidence.ProbeAttempts ??= [];

        return parsed;
    }

    private async Task AttemptVmCleanupAsync(
        DiscoveryModeSettings settings,
        IProgress<DiscoveryProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Report(progress, DiscoveryProgressStage.RevertingVm, "Cleaning up discovery VM state.");
            await StopVmIfRunningAsync(settings, cancellationToken);
            await RestoreCheckpointAsync(settings, cancellationToken);
            _logger.Log($"VM cleanup complete. VM='{settings.VmName}', Checkpoint='{settings.CheckpointName}'.");
        }
        catch (Exception ex)
        {
            _logger.Log($"VM cleanup/revert failed: {ex.Message}");
            Report(progress, DiscoveryProgressStage.RevertingVm, $"Cleanup warning: {ex.Message}", isError: true);
        }
    }

    private async Task StopVmIfRunningAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $vm = Get-VM -Name '__VM_NAME__' -ErrorAction Stop
        if ($vm.State -ne 'Off') {
            Stop-VM -Name '__VM_NAME__' -TurnOff -Force -Confirm:$false -ErrorAction Stop
        }
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            settings.CommandTimeoutSeconds,
            "Stop-VM",
            cancellationToken);
    }

    private async Task<string> GetVmStateAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        (Get-VM -Name '__VM_NAME__' -ErrorAction Stop).State.ToString()
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal);

        var result = await RunPowerShellAsync(script, settings.CommandTimeoutSeconds, cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "Unknown"
            : result.StandardOutput.Trim().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries).Last();
    }

    private async Task<string> GetHeartbeatStatusAsync(DiscoveryModeSettings settings, CancellationToken cancellationToken)
    {
        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $service = Get-VMIntegrationService -VMName '__VM_NAME__' -Name 'Heartbeat' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            'Heartbeat integration service not found.'
            exit 0
        }
        $service.PrimaryStatusDescription
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal);

        var result = await RunPowerShellAsync(script, settings.CommandTimeoutSeconds, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "No heartbeat output."
            : result.StandardOutput.Trim().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries).Last();
    }

    private async Task CopyFileToGuestAsync(
        DiscoveryModeSettings settings,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Host file to copy into guest is missing.", sourcePath);
        }

        var script = """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        Copy-VMFile -Name '__VM_NAME__' -SourcePath '__SOURCE_PATH__' -DestinationPath '__DESTINATION_PATH__' -FileSource Host -CreateFullPath -Force -ErrorAction Stop
        """
        .Replace("__VM_NAME__", EscapeForSingleQuotedPowerShell(settings.VmName), StringComparison.Ordinal)
        .Replace("__SOURCE_PATH__", EscapeForSingleQuotedPowerShell(sourcePath), StringComparison.Ordinal)
        .Replace("__DESTINATION_PATH__", EscapeForSingleQuotedPowerShell(destinationPath), StringComparison.Ordinal);

        await RunPowerShellStepAsync(
            script,
            settings.CommandTimeoutSeconds,
            $"Copy-VMFile '{sourcePath}' -> '{destinationPath}'",
            cancellationToken);

        _logger.Log($"Copied file to guest. Source='{sourcePath}', Destination='{destinationPath}'.");
    }

    private async Task RunPowerShellStepAsync(
        string script,
        int timeoutSeconds,
        string operationName,
        CancellationToken cancellationToken)
    {
        var result = await RunPowerShellAsync(script, timeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException($"{operationName} failed (exit {result.ExitCode}). {error}".Trim());
        }
    }

    private async Task<PowerShellExecutionResult> RunPowerShellAsync(
        string script,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"appcatalogue_discovery_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tempFilePath, script, Encoding.UTF8, cancellationToken);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{tempFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start PowerShell process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

            var completedTask = await Task.WhenAny(waitTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort process cleanup.
                }

                throw new TimeoutException($"PowerShell command timed out after {timeoutSeconds} seconds.");
            }

            await waitTask;

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            return new PowerShellExecutionResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }

    private static string NormalizeWindowsDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().TrimEnd('\\');
    }

    private static string CombineWindowsPath(string root, string child)
    {
        var normalizedRoot = NormalizeWindowsDirectory(root);
        var normalizedChild = child.Trim().TrimStart('\\');
        return string.IsNullOrWhiteSpace(normalizedRoot)
            ? normalizedChild
            : $"{normalizedRoot}\\{normalizedChild}";
    }

    private static string ToPathInsideOsVolume(string windowsPath)
    {
        var normalized = NormalizeWindowsDirectory(windowsPath).Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('\\');
    }

    private static string EscapeForSingleQuotedPowerShell(string input)
    {
        return (input ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    }

    private void Report(
        IProgress<DiscoveryProgressUpdate>? progress,
        DiscoveryProgressStage stage,
        string message,
        bool isError = false)
    {
        _logger.Log($"Discovery [{stage}] {message}");
        progress?.Report(new DiscoveryProgressUpdate
        {
            Stage = stage,
            Message = message,
            IsError = isError,
            TimestampUtc = DateTime.UtcNow
        });
    }

    private sealed class GuestDiscoverySignal
    {
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool ResultReady { get; set; }
        public string JobId { get; set; } = string.Empty;
        public bool? Success { get; set; }
        public string Error { get; set; } = string.Empty;
        public string ResultPath { get; set; } = string.Empty;
        public string LogPath { get; set; } = string.Empty;
        public string UpdatedUtc { get; set; } = string.Empty;
    }

    private sealed record StagedDiscoveryArtifacts(
        string JobId,
        string HostStagingJobDirectory,
        string StagedInstallerPath,
        string JobPath,
        string TriggerPath,
        string HostResultDirectory,
        string HostGuestArtifactsDirectory,
        string LegacyResultDirectory,
        string RawResultPath,
        string GuestStatusPath,
        string GuestLogPath);

    private sealed record CollectedGuestArtifacts(
        string ArtifactsDirectory,
        string LogPath,
        string StatusPath,
        string ResultPath);

    private sealed record PowerShellExecutionResult(int ExitCode, string StandardOutput, string StandardError);
}
