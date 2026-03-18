using System.IO;
using System.Text;
using System.Windows;
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
            DiscoveryProgressTextBox.Text = string.Empty;
            DiscoveryEvidenceTextBox.Text = string.Empty;
            DiscoverySilentSuggestionsListBox.ItemsSource = null;
            DiscoveryPrimaryRecommendationTextBlock.Text = string.Empty;
            DiscoverySecondaryRecommendationTextBlock.Text = string.Empty;
            DetectionTestResultTextBox.Text = string.Empty;
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
            SetStatus(runResult.Success
                ? "One-click discovery complete. Review and apply suggestions."
                : "One-click discovery finished with issues. Review details and logs.");
        }
        catch (Exception ex)
        {
            _logger.Log($"One-click discovery failed: {ex.Message}");
            AppendDiscoveryProgress(new DiscoveryProgressUpdate
            {
                Stage = DiscoveryProgressStage.Failed,
                Message = ex.Message,
                IsError = true
            });
            DiscoveryResultSummaryTextBlock.Text = $"Discovery failed: {ex.Message}";
            SetStatus("One-click discovery failed.");
        }
        finally
        {
            _isDiscoveryRunning = false;
            UpdateSourceTypeUi();
        }
    }

    private void ApplyDiscoverySuggestionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastHyperVDiscoveryResult?.DiscoveryResult is null)
        {
            ShowValidation("Run One-Click Discovery first.");
            return;
        }

        var result = _lastHyperVDiscoveryResult.DiscoveryResult;

        if (result.PrimaryDetection is not null && TryParseDetectionType(result.PrimaryDetection.Type, out var primaryType))
        {
            DetectionTypeComboBox.SelectedItem = primaryType;
            DetectionValueTextBox.Text = result.PrimaryDetection.Value;
        }

        if (result.SecondaryDetection is not null && TryParseDetectionType(result.SecondaryDetection.Type, out var secondaryType))
        {
            SecondaryDetectionTypeComboBox.SelectedItem = secondaryType;
            SecondaryDetectionValueTextBox.Text = result.SecondaryDetection.Value;
        }

        if (result.SilentSwitchSuggestions.Count > 0)
        {
            var preferredSilent = result.SilentSwitchSuggestions
                .OrderByDescending(s => ConfidenceScore(s.Confidence))
                .Select(s => s.Arguments)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(preferredSilent))
            {
                SilentArgumentsTextBox.Text = preferredSilent;
            }
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();
        SetStatus("Discovery suggestions applied to fields. Review and save when ready.");
    }

    private void AppendDiscoveryProgress(DiscoveryProgressUpdate update)
    {
        var localTime = update.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        var prefix = update.IsError ? "ERROR" : update.Stage.ToString();
        DiscoveryProgressTextBox.AppendText($"[{localTime}] {prefix}: {update.Message}{Environment.NewLine}");
        DiscoveryProgressTextBox.ScrollToEnd();
    }

    private void ApplyDiscoveryRunResultToUi(HyperVDiscoveryRunResult runResult)
    {
        var result = runResult.DiscoveryResult ?? new HyperVDiscoveryResult();
        DiscoveryResultSummaryTextBlock.Text =
            $"{runResult.Summary}{Environment.NewLine}" +
            $"Result file: {runResult.RawResultPath}";

        DiscoverySilentSuggestionsListBox.ItemsSource = result.SilentSwitchSuggestions;
        DiscoveryPrimaryRecommendationTextBlock.Text = result.PrimaryDetection?.DisplayText ?? "No primary recommendation";
        DiscoverySecondaryRecommendationTextBlock.Text = result.SecondaryDetection?.DisplayText ?? "No secondary recommendation";
        DiscoveryEvidenceTextBox.Text = BuildDiscoveryEvidenceText(result);

        if (!string.IsNullOrWhiteSpace(result.RawHelpOutput))
        {
            ProbeOutputTextBox.Text = result.RawHelpOutput;
        }

        if (result.SilentSwitchSuggestions.Count > 0)
        {
            SuggestionComboBox.ItemsSource = result.SilentSwitchSuggestions.Select(s => s.Arguments).Distinct().ToList();
            SuggestionComboBox.SelectedIndex = 0;
        }

        var detectionSuggestions = BuildDetectionSuggestionsFromDiscovery(result);
        if (detectionSuggestions.Count > 0)
        {
            DetectionSuggestionsListBox.ItemsSource = detectionSuggestions;
            DetectionSuggestionsListBox.SelectedIndex = 0;
            _lastDetectionSuggestionResult = new DetectionSuggestionResult
            {
                Summary = "Hyper-V discovery produced suggested detection rules. Review before applying.",
                Suggestions = detectionSuggestions,
                RecommendedPrimaryDetection = detectionSuggestions.FirstOrDefault(),
                RecommendedSecondaryDetection = detectionSuggestions.Skip(1).FirstOrDefault()
            };
            DetectionSuggestionSummaryTextBlock.Text = _lastDetectionSuggestionResult.Summary;
        }

        if (result.Errors.Count > 0)
        {
            DetectionTestResultTextBox.Text =
                "Discovery warnings/errors:" + Environment.NewLine +
                string.Join(Environment.NewLine, result.Errors);
        }
        else
        {
            DetectionTestResultTextBox.Text =
                "Discovery completed. Suggestions are editable and only applied when you click Apply Suggestions.";
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

