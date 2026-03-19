using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using AppCatalogue.Shared.Models;
using AppCatalogue.Shared.Services;

namespace AppCatalogueAdmin;

public partial class MainWindow
{
    private async Task RunHyperVDiscoveryAsync()
    {
        if (_isDiscoveryRunning)
        {
            return;
        }

        if (!SecurityHelper.IsAdministrator())
        {
            ShowValidation(
                "One-Click Discovery requires administrator rights on the host. " +
                "Please close AppCatalogueAdmin and re-open it with Run as administrator.");
            return;
        }

        var app = BuildSuggestionContextEntry();
        if (app.InstallerSourceType != InstallerSourceType.FileServer)
        {
            ShowValidation("One-Click Discovery requires a FileServer installer entry.");
            return;
        }

        var installerPath = Environment.ExpandEnvironmentVariables(app.InstallerPath);
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            ShowValidation("Installer path is missing or not found.");
            return;
        }

        var confirmation = MessageBox.Show(
            "One-Click Discovery will restore checkpoint 'CleanState', run the installer inside the packaging VM, collect suggestions, then revert the VM. Continue?",
            "One-Click Discovery",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _isDiscoveryRunning = true;
            _lastHyperVDiscoveryResult = null;
            ResetDiscoveryDiagnosticsUi();
            SetDiscoveryStateBadge("In Progress", "#1B73C7");
            SetConfidenceBadge("-", "#7A8797");
            UpdateSourceTypeUi();

            var settings = BuildDiscoverySettingsFromUi();
            _discoverySettings = settings;
            _discoverySettingsService.Save(AppPaths.DiscoverySettingsFilePath, settings);

            var request = new HyperVDiscoveryRequest
            {
                AppName = app.Name,
                InstallerPath = installerPath,
                PreferredSilentArguments = app.SilentArguments,
                Settings = settings,
                GuestScriptSourceDirectory = ResolveGuestScriptSourceDirectory()
            };

            SetStatus("Preparing lab VM for one-click discovery...");
            var progress = new Progress<DiscoveryProgressUpdate>(AppendDiscoveryProgress);
            var runResult = await _hyperVDiscoveryService.RunDiscoveryAsync(request, progress);
            _lastHyperVDiscoveryResult = runResult;

            ApplyDiscoveryRunResultToUi(runResult);
        }
        catch (Exception ex)
        {
            var guidance = ex.Message;
            if (guidance.Contains("required permission", StringComparison.OrdinalIgnoreCase) ||
                guidance.Contains("Hyper-V permissions", StringComparison.OrdinalIgnoreCase))
            {
                guidance += Environment.NewLine +
                            "Run AppCatalogueAdmin as Administrator and ensure your account is in local 'Hyper-V Administrators'.";
            }

            _logger.Log($"One-click discovery failed: {guidance}");
            AppendDiscoveryProgress(new DiscoveryProgressUpdate
            {
                Stage = DiscoveryProgressStage.Failed,
                Message = guidance,
                IsError = true
            });
            DiscoveryResultSummaryTextBlock.Text = $"Discovery failed: {guidance}";
            SetDiscoveryStateBadge("Failed", "#B6433B");
            SetConfidenceBadge("Manual Review", "#B46A00");
            SetStatus("One-click discovery failed.");
        }
        finally
        {
            _isDiscoveryRunning = false;
            UpdateSourceTypeUi();
        }
    }

    private void ResetDiscoveryDiagnosticsUi()
    {
        DiscoveryProgressTextBox.Text = string.Empty;
        DiscoveryEvidenceTextBox.Text = string.Empty;
        DiscoverySilentSuggestionsListBox.ItemsSource = null;
        DiscoveryAttemptHistoryListBox.ItemsSource = null;
        DiscoveryPrimaryRecommendationTextBlock.Text = string.Empty;
        DiscoverySecondaryRecommendationTextBlock.Text = string.Empty;
        DiscoveryResultSummaryTextBlock.Text = string.Empty;
        DiscoveryRecommendedCommandTextBox.Text = string.Empty;
        DiscoveryHostJobPathTextBox.Text = string.Empty;
        DiscoveryHostLogPathTextBox.Text = string.Empty;
        DiscoveryGuestLogPathTextBox.Text = string.Empty;
        DiscoveryStageTextBlock.Text = "Current stage: Not started";
        DiscoveryFinalStageTextBlock.Text = "Final stage: Not started";
    }

    private void AppendDiscoveryProgress(DiscoveryProgressUpdate update)
    {
        var localTime = update.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        var prefix = update.IsError ? "ERROR" : update.Stage.ToString();
        DiscoveryProgressTextBox.AppendText($"[{localTime}] {prefix}: {update.Message}{Environment.NewLine}");
        DiscoveryProgressTextBox.ScrollToEnd();
        DiscoveryStageTextBlock.Text = $"Current stage: {update.Stage}";
    }

    private void ApplyDiscoveryRunResultToUi(HyperVDiscoveryRunResult runResult)
    {
        var result = runResult.DiscoveryResult ?? new HyperVDiscoveryResult();
        var status = runResult.Status;

        var statusMessage = status is null
            ? runResult.Summary
            : $"{status.State} - {status.Stage}: {status.Message}";
        if (status is not null && !string.IsNullOrWhiteSpace(status.Error))
        {
            statusMessage += $"{Environment.NewLine}Error: {status.Error}";
        }

        DiscoveryResultSummaryTextBlock.Text =
            $"{runResult.Summary}{Environment.NewLine}" +
            $"Status: {statusMessage}{Environment.NewLine}" +
            $"Result file: {runResult.RawResultPath}";
        DiscoveryStageTextBlock.Text = $"Current stage: {runResult.CurrentStage}";
        DiscoveryFinalStageTextBlock.Text = $"Final stage: {runResult.FinalStage}";
        DiscoveryHostJobPathTextBox.Text = runResult.HostJobDirectory;
        DiscoveryHostLogPathTextBox.Text = runResult.HostLogPath;
        DiscoveryGuestLogPathTextBox.Text = runResult.GuestLogPath;
        DiscoveryRecommendedCommandTextBox.Text = BuildSilentRecommendationText(result);

        DiscoverySilentSuggestionsListBox.ItemsSource = result.SilentSwitchSuggestions;
        DiscoveryAttemptHistoryListBox.ItemsSource = result.SilentSwitchAttemptHistory
            .Select(BuildAttemptHistoryDisplay)
            .ToList();
        DiscoveryPrimaryRecommendationTextBlock.Text = result.PrimaryDetection?.DisplayText ?? "No primary recommendation";
        DiscoverySecondaryRecommendationTextBlock.Text = result.SecondaryDetection?.DisplayText ?? "No secondary recommendation";
        DiscoveryEvidenceTextBox.Text = BuildDiscoveryEvidenceText(result);

        SetDiscoveryStateBadge(runResult.Success ? "Completed" : "Needs Review", runResult.Success ? "#1F8656" : "#B46A00");
        var confidenceLabel = string.IsNullOrWhiteSpace(result.SilentRecommendation?.ConfidenceLabel)
            ? "-"
            : result.SilentRecommendation.ConfidenceLabel;
        SetConfidenceBadge(confidenceLabel, ConfidenceColor(confidenceLabel));

        if (!string.IsNullOrWhiteSpace(result.RawHelpOutput))
        {
            ProbeOutputTextBox.Text = result.RawHelpOutput;
        }

        SuggestionComboBox.ItemsSource = result.SilentSwitchSuggestions.Select(s => s.Arguments).Distinct().ToList();
        if (result.SilentSwitchSuggestions.Count > 0)
        {
            SuggestionComboBox.SelectedIndex = 0;
        }
        else
        {
            SuggestionComboBox.SelectedItem = null;
        }

        var detectionSuggestions = BuildDetectionSuggestionsFromDiscovery(result);
        if (detectionSuggestions.Count > 0)
        {
            DetectionSuggestionsListBox.ItemsSource = detectionSuggestions;
            DetectionSuggestionsListBox.SelectedIndex = 0;
            _lastDetectionSuggestionResult = new DetectionSuggestionResult
            {
                Summary = "Hyper-V discovery produced suggested detection rules. High-confidence values are auto-applied when possible.",
                Suggestions = detectionSuggestions,
                RecommendedPrimaryDetection = detectionSuggestions.FirstOrDefault(),
                RecommendedSecondaryDetection = detectionSuggestions.Skip(1).FirstOrDefault()
            };
            DetectionSuggestionSummaryTextBlock.Text = _lastDetectionSuggestionResult.Summary;
        }
        else
        {
            DetectionSuggestionSummaryTextBlock.Text =
                "No reliable detection suggestions were returned from discovery. Configure detection manually.";
        }

        if (runResult.Success)
        {
            if (TryAutoApplyDiscoverySuggestions(result, out var autoApplySummary))
            {
                DiscoveryResultSummaryTextBlock.Text += Environment.NewLine + autoApplySummary;
                SetStatus("One-click discovery complete. High-confidence values were auto-applied.");
            }
            else
            {
                SetStatus("One-click discovery complete. Review suggestions and adjust manually if needed.");
            }
        }
        else
        {
            if (result.Errors.Count > 0)
            {
                DetectionSuggestionSummaryTextBlock.Text += Environment.NewLine +
                                                          "Warnings: " +
                                                          string.Join(" | ", result.Errors.Take(3));
            }

            SetStatus("One-click discovery finished with issues. Review details and logs.");
        }
    }

    private DiscoveryModeSettings BuildDiscoverySettingsFromUi()
    {
        var defaults = DiscoverySettingsService.CreateDefaults();
        return new DiscoveryModeSettings
        {
            VmName = string.IsNullOrWhiteSpace(VmNameTextBox.Text) ? defaults.VmName : VmNameTextBox.Text.Trim(),
            CheckpointName = string.IsNullOrWhiteSpace(CheckpointNameTextBox.Text) ? defaults.CheckpointName : CheckpointNameTextBox.Text.Trim(),
            GuestInputDirectory = defaults.GuestInputDirectory,
            GuestOutputDirectory = defaults.GuestOutputDirectory,
            GuestScriptsDirectory = defaults.GuestScriptsDirectory,
            HostStagingDirectory = AppPaths.DiscoveryHostStagingDirectory,
            HostResultsDirectory = AppPaths.DiscoveryResultsDirectory,
            GuestReadyTimeoutSeconds = defaults.GuestReadyTimeoutSeconds,
            DiscoveryTimeoutSeconds = defaults.DiscoveryTimeoutSeconds,
            CommandTimeoutSeconds = defaults.CommandTimeoutSeconds,
            ProbeTimeoutSeconds = defaults.ProbeTimeoutSeconds,
            InstallerTimeoutSeconds = defaults.InstallerTimeoutSeconds,
            ShutdownVmOnComplete = true
        };
    }

    private static int ConfidenceScore(string confidence)
    {
        return confidence.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            _ => 1
        };
    }

    private static string BuildSilentRecommendationText(HyperVDiscoveryResult result)
    {
        var recommendation = result.SilentRecommendation ?? new SilentSwitchRecommendation();
        if (string.IsNullOrWhiteSpace(recommendation.RecommendedCommand) &&
            string.IsNullOrWhiteSpace(recommendation.RecommendedArguments))
        {
            return "No trusted silent command recommendation was validated. Manual review is required.";
        }

        var commandText = $"{recommendation.RecommendedCommand} {recommendation.RecommendedArguments}".Trim();
        return $"{commandText}{Environment.NewLine}Confidence: {recommendation.ConfidenceLabel} ({recommendation.ConfidenceScore:0.00}){Environment.NewLine}{recommendation.Reason}";
    }

    private static string BuildAttemptHistoryDisplay(SilentSwitchAttemptRecord attempt)
    {
        var exitCodeText = attempt.ExitCode.HasValue ? attempt.ExitCode.Value.ToString() : "n/a";
        return $"#{attempt.AttemptNumber} [{attempt.CandidateSource}] {attempt.Arguments} | Pass={attempt.Pass} Exit={exitCodeText} TimedOut={attempt.TimedOut} Artifacts={attempt.InstallationArtifactsDetected}";
    }

    private void SetDiscoveryStateBadge(string text, string backgroundHex)
    {
        DiscoveryStateBadgeTextBlock.Text = text;
        DiscoveryStateBadgeBorder.Background = BrushFromHex(backgroundHex, "#E4ECF5");
        DiscoveryStateBadgeTextBlock.Foreground = text.Equals("Ready", StringComparison.OrdinalIgnoreCase) || text.Equals("-", StringComparison.OrdinalIgnoreCase)
            ? BrushFromHex("#23405D", "#23405D")
            : BrushFromHex("#FFFFFF", "#FFFFFF");
    }

    private void SetConfidenceBadge(string text, string backgroundHex)
    {
        DiscoveryConfidenceBadgeTextBlock.Text = text;
        DiscoveryConfidenceBadgeBorder.Background = BrushFromHex(backgroundHex, "#E4ECF5");
        DiscoveryConfidenceBadgeTextBlock.Foreground = text.Equals("-", StringComparison.OrdinalIgnoreCase)
            ? BrushFromHex("#23405D", "#23405D")
            : BrushFromHex("#FFFFFF", "#FFFFFF");
    }

    private static string ConfidenceColor(string confidenceLabel)
    {
        return confidenceLabel.ToLowerInvariant() switch
        {
            "high" => "#1F8656",
            "medium" => "#B46A00",
            _ => "#7A8797"
        };
    }

    private static Brush BrushFromHex(string candidateHex, string fallbackHex)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(candidateHex)!;
        }
        catch
        {
            return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
        }
    }

    private bool TryAutoApplyDiscoverySuggestions(HyperVDiscoveryResult result, out string summary)
    {
        var applied = new List<string>();
        var changed = false;

        var recommendedArguments = GetHighConfidenceSilentArguments(result);
        if (!string.IsNullOrWhiteSpace(recommendedArguments) &&
            !string.Equals(SilentArgumentsTextBox.Text.Trim(), recommendedArguments, StringComparison.OrdinalIgnoreCase))
        {
            SilentArgumentsTextBox.Text = recommendedArguments;
            applied.Add($"Silent arguments set to '{recommendedArguments}'.");
            changed = true;
        }

        if (TryGetDetectionCandidate(result.PrimaryDetection, allowMediumConfidence: true, out var primaryType, out var primaryValue))
        {
            if (DetectionTypeComboBox.SelectedItem is not DetectionType selectedPrimaryType ||
                selectedPrimaryType != primaryType ||
                !string.Equals(DetectionValueTextBox.Text.Trim(), primaryValue, StringComparison.OrdinalIgnoreCase))
            {
                DetectionTypeComboBox.SelectedItem = primaryType;
                DetectionValueTextBox.Text = primaryValue;
                applied.Add($"Primary detection set to {primaryType}: {primaryValue}.");
                changed = true;
            }
        }

        if (TryGetDetectionCandidate(result.SecondaryDetection, allowMediumConfidence: false, out var secondaryType, out var secondaryValue))
        {
            if (SecondaryDetectionTypeComboBox.SelectedItem is not DetectionType selectedSecondaryType ||
                selectedSecondaryType != secondaryType ||
                !string.Equals(SecondaryDetectionValueTextBox.Text.Trim(), secondaryValue, StringComparison.OrdinalIgnoreCase))
            {
                SecondaryDetectionTypeComboBox.SelectedItem = secondaryType;
                SecondaryDetectionValueTextBox.Text = secondaryValue;
                applied.Add($"Secondary detection set to {secondaryType}: {secondaryValue}.");
                changed = true;
            }
        }

        if (!changed)
        {
            summary = "No obvious high-confidence values were auto-applied.";
            return false;
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();

        summary = "Auto-applied from discovery: " + string.Join(" ", applied);
        return true;
    }

    private static string GetHighConfidenceSilentArguments(HyperVDiscoveryResult result)
    {
        if (result.SilentRecommendation is not null &&
            !string.IsNullOrWhiteSpace(result.SilentRecommendation.RecommendedArguments) &&
            !result.SilentRecommendation.ManualReviewNeeded &&
            (result.SilentRecommendation.ConfidenceScore >= 0.85 ||
             IsHighConfidence(result.SilentRecommendation.ConfidenceLabel)))
        {
            return result.SilentRecommendation.RecommendedArguments.Trim();
        }

        return result.SilentSwitchSuggestions
            .Where(suggestion =>
                !string.IsNullOrWhiteSpace(suggestion.Arguments) &&
                IsHighConfidence(suggestion.Confidence))
            .OrderByDescending(suggestion => ConfidenceScore(suggestion.Confidence))
            .Select(suggestion => suggestion.Arguments.Trim())
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool TryGetDetectionCandidate(
        DetectionRecommendation? recommendation,
        bool allowMediumConfidence,
        out DetectionType detectionType,
        out string detectionValue)
    {
        detectionType = DetectionType.RegistryDisplayName;
        detectionValue = string.Empty;

        if (recommendation is null ||
            string.IsNullOrWhiteSpace(recommendation.Value) ||
            !TryParseDetectionType(recommendation.Type, out detectionType))
        {
            return false;
        }

        var confidence = recommendation.Confidence ?? string.Empty;
        if (!IsHighConfidence(confidence) &&
            (!allowMediumConfidence || !confidence.Equals("Medium", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        detectionValue = recommendation.Value.Trim();
        return !string.IsNullOrWhiteSpace(detectionValue);
    }

    private static bool IsHighConfidence(string confidence)
    {
        return confidence.Equals("High", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenDiscoveryJobFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = string.IsNullOrWhiteSpace(DiscoveryHostJobPathTextBox.Text)
            ? _lastHyperVDiscoveryResult?.HostJobDirectory ?? string.Empty
            : DiscoveryHostJobPathTextBox.Text.Trim();
        OpenFolderPath(path);
    }

    private void OpenDiscoveryLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var candidatePaths = new[]
        {
            _lastHyperVDiscoveryResult?.HostLogPath ?? string.Empty,
            _lastHyperVDiscoveryResult?.GuestLogPath ?? string.Empty,
            AppPaths.AdminLogPath
        };

        var firstExistingParent = candidatePaths
            .Select(path => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty)
            .FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory));

        OpenFolderPath(firstExistingParent ?? string.Empty);
    }

    private void OpenFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowValidation("No log or job folder is available yet.");
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var targetFolder = Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
        {
            ShowValidation($"Folder not found: {expanded}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{targetFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to open folder '{targetFolder}': {ex.Message}");
            ShowValidation($"Could not open folder: {targetFolder}");
        }
    }

    private string ResolveGuestScriptSourceDirectory()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DiscoveryScripts", "Guest"),
            Path.Combine(Environment.CurrentDirectory, "DiscoveryScripts", "Guest"),
            Path.Combine(Environment.CurrentDirectory, "AppCatalogueAdmin", "DiscoveryScripts", "Guest")
        };

        foreach (var candidate in candidatePaths)
        {
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Run-Discovery.ps1")) &&
                File.Exists(Path.Combine(candidate, "Discovery-Watcher.ps1")) &&
                File.Exists(Path.Combine(candidate, "Discovery-Logging.ps1")) &&
                File.Exists(Path.Combine(candidate, "Install-DiscoveryBootstrap.ps1")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Guest discovery script package was not found. Expected DiscoveryScripts\\Guest next to AppCatalogueAdmin.");
    }

    private static List<DetectionRuleSuggestion> BuildDetectionSuggestionsFromDiscovery(HyperVDiscoveryResult result)
    {
        var suggestions = new List<DetectionRuleSuggestion>();

        if (result.PrimaryDetection is not null && TryParseDetectionType(result.PrimaryDetection.Type, out var primaryType))
        {
            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = primaryType,
                DetectionValue = result.PrimaryDetection.Value,
                Confidence = result.PrimaryDetection.Confidence,
                Reason = result.PrimaryDetection.Reason
            });
        }

        if (result.SecondaryDetection is not null && TryParseDetectionType(result.SecondaryDetection.Type, out var secondaryType))
        {
            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = secondaryType,
                DetectionValue = result.SecondaryDetection.Value,
                Confidence = result.SecondaryDetection.Confidence,
                Reason = result.SecondaryDetection.Reason
            });
        }

        foreach (var uninstallEntry in result.Evidence.NewUninstallEntries)
        {
            if (string.IsNullOrWhiteSpace(uninstallEntry))
            {
                continue;
            }

            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = DetectionType.RegistryDisplayName,
                DetectionValue = uninstallEntry,
                Confidence = "Medium",
                Reason = "Derived from discovery uninstall evidence."
            });
        }

        foreach (var filePath in result.Evidence.NewFiles.Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Take(5))
        {
            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = DetectionType.FileExists,
                DetectionValue = filePath,
                Confidence = "Medium",
                Reason = "Derived from discovery file evidence."
            });
        }

        return suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.DetectionValue))
            .GroupBy(s => $"{s.DetectionType}|{s.DetectionValue}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(item => ConfidenceScore(item.Confidence)).First())
            .ToList();
    }

    private static bool TryParseDetectionType(string value, out DetectionType detectionType)
    {
        if (Enum.TryParse(value, ignoreCase: true, out detectionType))
        {
            return true;
        }

        detectionType = DetectionType.RegistryDisplayName;
        return false;
    }

    private static string BuildDiscoveryEvidenceText(HyperVDiscoveryResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("New uninstall entries:");
        if (result.Evidence.NewUninstallEntries.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewUninstallEntries)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New executable/file evidence:");
        if (result.Evidence.NewFiles.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewFiles.Take(20))
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New registry key evidence:");
        if (result.Evidence.NewRegistryKeys.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewRegistryKeys)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New services:");
        if (result.Evidence.NewServices.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewServices)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New shortcuts:");
        if (result.Evidence.NewShortcuts.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewShortcuts)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("New processes:");
        if (result.Evidence.NewProcesses.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.NewProcesses)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Probe attempts:");
        if (result.Evidence.ProbeAttempts.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in result.Evidence.ProbeAttempts)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.InstallAttemptSummary))
        {
            builder.AppendLine();
            builder.AppendLine("Install attempt summary:");
            builder.AppendLine(result.InstallAttemptSummary);
        }

        return builder.ToString().Trim();
    }
}
