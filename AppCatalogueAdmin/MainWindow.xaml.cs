using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AppCatalogue.Shared.Models;
using AppCatalogue.Shared.Services;
using Microsoft.Win32;

namespace AppCatalogueAdmin;

public partial class MainWindow : Window
{
    private readonly FileLogger _logger;
    private readonly AppConfigService _configService;
    private readonly SilentSwitchProbeService _silentProbeService;
    private readonly DetectionSuggestionService _detectionSuggestionService;
    private readonly AppDetectionService _detectionService;
    private readonly ObservableCollection<AppEntry> _apps = [];
    private readonly ICollectionView _appsView;
    private bool _isDirty;
    private bool _isLoadingState;
    private string _lastImportedRepositoryFolder = string.Empty;
    private DetectionSuggestionResult? _lastDetectionSuggestionResult;

    public MainWindow()
    {
        InitializeComponent();

        AppPaths.EnsureEndpointStructure();
        _logger = new FileLogger(AppPaths.AdminLogPath);
        _configService = new AppConfigService(_logger);
        _silentProbeService = new SilentSwitchProbeService(_logger);
        _detectionSuggestionService = new DetectionSuggestionService(_logger);
        _detectionService = new AppDetectionService();

        AppPaths.EnsureFileServerStructure(_logger);
        ConfigPathTextBlock.Text = $"Config Path: {AppPaths.FileServerConfigFilePath}";

        InstallerSourceTypeComboBox.ItemsSource = Enum.GetValues<InstallerSourceType>();
        DetectionTypeComboBox.ItemsSource = Enum.GetValues<DetectionType>();
        SecondaryDetectionTypeComboBox.ItemsSource = Enum.GetValues<DetectionType>();

        _appsView = CollectionViewSource.GetDefaultView(_apps);
        _appsView.Filter = FilterApps;
        AppsDataGrid.ItemsSource = _appsView;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConfigFromDisk();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _appsView.Refresh();
    }

    private void ConfigVersionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoadingState)
        {
            MarkDirty(true);
        }
    }

    private void AppsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppsDataGrid.SelectedItem is not AppEntry selected)
        {
            return;
        }

        PopulateEditor(selected);
        SetStatus($"Editing: {selected.Name}");
    }

    private void NewAppButton_Click(object sender, RoutedEventArgs e)
    {
        AppsDataGrid.SelectedItem = null;
        ClearEditor();
        SetStatus("New app template ready.");
    }

    private void SaveAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildAppFromForm(out var app, out var error))
        {
            ShowValidation(error);
            return;
        }

        if (AppsDataGrid.SelectedItem is AppEntry selected)
        {
            if (_apps.Any(existing =>
                    !ReferenceEquals(existing, selected) &&
                    existing.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowValidation($"An app named '{app.Name}' already exists.");
                return;
            }

            ApplyChanges(selected, app);
            _appsView.Refresh();
            AppsDataGrid.Items.Refresh();
            SetStatus($"Updated app: {selected.Name}");
        }
        else
        {
            if (_apps.Any(existing => existing.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowValidation($"An app named '{app.Name}' already exists.");
                return;
            }

            _apps.Add(app);
            _appsView.Refresh();
            AppsDataGrid.SelectedItem = app;
            SetStatus($"Added app: {app.Name}");
        }

        MarkDirty(true);
    }

    private void DeleteAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsDataGrid.SelectedItem is not AppEntry selected)
        {
            ShowValidation("Select an app to delete.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete '{selected.Name}'?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _apps.Remove(selected);
        MarkDirty(true);
        SetStatus("App deleted.");

        if (_apps.Count > 0)
        {
            AppsDataGrid.SelectedIndex = 0;
        }
        else
        {
            ClearEditor();
        }
    }

    private void DuplicateAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsDataGrid.SelectedItem is not AppEntry selected)
        {
            ShowValidation("Select an app to duplicate.");
            return;
        }

        var duplicate = Clone(selected);
        duplicate.Name = BuildDuplicateName(selected.Name);
        _apps.Add(duplicate);
        _appsView.Refresh();
        AppsDataGrid.SelectedItem = duplicate;
        MarkDirty(true);
        SetStatus($"Duplicated app: {duplicate.Name}");
    }

    private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var errors = ValidateAll();
        if (errors.Count > 0)
        {
            ShowValidation("Validation failed:\n\n" + string.Join(Environment.NewLine, errors));
            SetStatus("Validation failed.");
            return;
        }

        try
        {
            AppPaths.EnsureFileServerStructure(_logger);
            var config = new AppCatalogueConfig
            {
                ConfigVersion = ConfigVersionTextBox.Text.Trim(),
                Apps = _apps.Select(Clone).ToList()
            };

            _configService.SaveConfig(config, AppPaths.FileServerConfigFilePath);
            MarkDirty(false);
            SetStatus($"Config saved with {_apps.Count} app entries.");
        }
        catch (Exception ex)
        {
            _logger.Log($"Save config failed: {ex.Message}");
            MessageBox.Show(
                $"Could not save config at:\n{AppPaths.FileServerConfigFilePath}\n\n{ex.Message}",
                "Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetStatus("Save failed.");
        }
    }

    private void ReloadConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            var confirm = MessageBox.Show(
                "Discard unsaved changes and reload from disk?",
                "Reload Config",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        LoadConfigFromDisk();
    }

    private void ValidateConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var errors = ValidateAll();
        if (errors.Count == 0)
        {
            SetStatus("Validation succeeded.");
            MessageBox.Show("Config validation passed.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetStatus("Validation failed.");
        MessageBox.Show(
            "Validation errors:\n\n" + string.Join(Environment.NewLine, errors),
            "Validation",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async void ImportInstallerButton_Click(object sender, RoutedEventArgs e)
    {
        await BrowseAndImportInstallerAsync();
    }

    private async void BrowseInstallerButton_Click(object sender, RoutedEventArgs e)
    {
        await BrowseAndImportInstallerAsync();
    }

    private async void DetectSilentSwitchesButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType sourceType &&
            sourceType == InstallerSourceType.Winget)
        {
            ProbeSummaryTextBlock.Text = "Silent switch probing is for file installers only.";
            SetStatus("Switch source type to FileServer to probe installer arguments.");
            return;
        }

        var installerPath = InstallerPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            ShowValidation("InstallerPath is required before probing for silent switches.");
            return;
        }

        var resolvedPath = Environment.ExpandEnvironmentVariables(installerPath);
        ProbeSummaryTextBlock.Text = "Probing installer help output...";
        ProbeOutputTextBox.Text = string.Empty;
        SuggestionComboBox.ItemsSource = null;
        SetStatus("Running silent switch helper...");

        try
        {
            var probeResult = await _silentProbeService.ProbeAsync(resolvedPath, timeoutMs: 5000);
            ProbeSummaryTextBlock.Text = $"{probeResult.Summary} Confidence: {probeResult.Confidence}";
            ProbeOutputTextBox.Text = probeResult.RawOutput;
            SuggestionComboBox.ItemsSource = probeResult.Suggestions;

            if (probeResult.Suggestions.Count > 0)
            {
                SuggestionComboBox.SelectedIndex = 0;
            }

            if (probeResult.IsMsi && probeResult.Suggestions.Count > 0)
            {
                SilentArgumentsTextBox.Text = probeResult.Suggestions[0];
                SetStatus("MSI detected. Silent arguments auto-populated.");
            }
            else if (probeResult.Suggestions.Count == 1 &&
                     !probeResult.Confidence.Equals("Low", StringComparison.OrdinalIgnoreCase))
            {
                SilentArgumentsTextBox.Text = probeResult.Suggestions[0];
                SetStatus("One strong suggestion found and applied.");
            }
            else
            {
                SetStatus("Silent switch detection completed. Review suggestions before saving.");
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Silent switch detection failed: {ex.Message}");
            ProbeSummaryTextBlock.Text = "Silent switch probing failed.";
            ProbeOutputTextBox.Text = ex.Message;
            SetStatus("Silent switch detection failed.");
        }

        UpdateCommandPreview();
        GenerateDetectionSuggestions(autoTriggered: true);
    }

    private void ImportDropZone_DragEnter(object sender, DragEventArgs e)
    {
        HandleDragFeedback(e);
    }

    private void ImportDropZone_DragOver(object sender, DragEventArgs e)
    {
        HandleDragFeedback(e);
    }

    private void ImportDropZone_DragLeave(object sender, DragEventArgs e)
    {
        SetDropZoneHighlight(false);
    }

    private async void ImportDropZone_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneHighlight(false);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        await ImportInstallerFileAsync(files[0]);
    }

    private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingState)
        {
            return;
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private void EditorField_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingState)
        {
            return;
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private void EditorField_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingState)
        {
            return;
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private void InstallerSourceTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSourceTypeUi();
        if (_isLoadingState)
        {
            return;
        }

        MarkDirty(true);
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private async void BrowseIconButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select icon file",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.ico|All files|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var selectedIconPath = dialog.FileName;
            var useWingetSource = InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType source &&
                                  source == InstallerSourceType.Winget;

            if (!useWingetSource)
            {
                var repositoryFolder = ResolveRepositoryFolderForCurrentApp();
                Directory.CreateDirectory(repositoryFolder);
                var targetIconPath = Path.Combine(repositoryFolder, Path.GetFileName(selectedIconPath));

                await Task.Run(() => File.Copy(selectedIconPath, targetIconPath, overwrite: true));
                IconPathTextBox.Text = targetIconPath;
                _logger.Log($"Copied icon '{selectedIconPath}' to repository '{targetIconPath}'.");
                SetStatus("Icon copied to repository.");
            }
            else
            {
                IconPathTextBox.Text = selectedIconPath;
                SetStatus("Icon path set.");
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Icon import failed: {ex.Message}");
            ShowValidation($"Icon import failed: {ex.Message}");
        }
    }

    private void SuggestionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommandPreview();
    }

    private void AcceptSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestionComboBox.SelectedItem is not string suggestion || string.IsNullOrWhiteSpace(suggestion))
        {
            ShowValidation("Select a suggestion first.");
            return;
        }

        SilentArgumentsTextBox.Text = suggestion;
        MarkDirty(true);
        UpdateCommandPreview();
        SetStatus("Silent argument suggestion applied.");
    }

    private void SuggestDetectionRuleButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateDetectionSuggestions(autoTriggered: false);
    }

    private void SetPrimarySuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (DetectionSuggestionsListBox.SelectedItem is not DetectionRuleSuggestion suggestion)
        {
            ShowValidation("Select a detection suggestion first.");
            return;
        }

        DetectionTypeComboBox.SelectedItem = suggestion.DetectionType;
        DetectionValueTextBox.Text = suggestion.DetectionValue;
        DetectionSuggestionSummaryTextBlock.Text =
            $"Primary detection set ({suggestion.Confidence}): {suggestion.Reason}";

        MarkDirty(true);
        SetStatus("Primary detection suggestion applied. Please review before saving.");
        UpdateDetectionExplanation();
    }

    private void SetSecondarySuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (DetectionSuggestionsListBox.SelectedItem is not DetectionRuleSuggestion suggestion)
        {
            ShowValidation("Select a detection suggestion first.");
            return;
        }

        SecondaryDetectionTypeComboBox.SelectedItem = suggestion.DetectionType;
        SecondaryDetectionValueTextBox.Text = suggestion.DetectionValue;
        DetectionSuggestionSummaryTextBlock.Text =
            $"Secondary detection set ({suggestion.Confidence}): {suggestion.Reason}";

        MarkDirty(true);
        SetStatus("Secondary detection suggestion applied. Please review before saving.");
        UpdateDetectionExplanation();
    }

    private void TestDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        var app = BuildSuggestionContextEntry();
        if (!AppValidator.TryValidate(app, out var validationError))
        {
            ShowValidation(validationError);
            return;
        }

        try
        {
            var result = _detectionService.EvaluateApp(app);
            DetectionTestResultTextBox.Text = BuildDetectionTestOutput(result);
            SetStatus(result.IsInstalled ? "Detection test: PASS" : "Detection test: FAIL");
            _logger.Log($"{app.Name}: detection test completed. Pass={result.IsInstalled}.");
        }
        catch (Exception ex)
        {
            DetectionTestResultTextBox.Text = $"Detection test failed: {ex.Message}";
            SetStatus("Detection test failed.");
            _logger.Log($"{app.Name}: detection test failed - {ex.Message}");
        }
    }

    private async void DiscoveryModeButton_Click(object sender, RoutedEventArgs e)
    {
        var app = BuildSuggestionContextEntry();
        if (app.InstallerSourceType != InstallerSourceType.FileServer)
        {
            ShowValidation("Discovery mode requires a FileServer installer entry.");
            return;
        }

        var installerPath = Environment.ExpandEnvironmentVariables(app.InstallerPath);
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            ShowValidation("Installer path is missing or not found for discovery mode.");
            return;
        }

        var confirm = MessageBox.Show(
            "Discovery mode will run the installer on this machine. Continue?",
            "Discovery Mode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetStatus("Discovery mode: capturing pre-install snapshot...");
            _logger.Log($"{app.Name}: discovery mode started.");

            var beforeUninstall = _detectionService.CaptureUninstallEntries();
            var beforePaths = CaptureFilePathSnapshot(app.Name);

            var exitCode = await RunInstallerForDiscoveryAsync(installerPath, app.SilentArguments);
            _logger.Log($"{app.Name}: discovery installer exited with code {exitCode}.");

            SetStatus("Discovery mode: capturing post-install snapshot...");
            var afterUninstall = _detectionService.CaptureUninstallEntries();
            var afterPaths = CaptureFilePathSnapshot(app.Name);

            var discoverySuggestions = BuildDiscoverySuggestions(app.Name, beforeUninstall, afterUninstall, beforePaths, afterPaths);
            if (discoverySuggestions.Count == 0)
            {
                var fallback = _detectionSuggestionService.Suggest(app, installerPath, ProbeOutputTextBox.Text);
                discoverySuggestions = fallback.Suggestions;
            }

            DetectionSuggestionsListBox.ItemsSource = discoverySuggestions;
            _lastDetectionSuggestionResult = new DetectionSuggestionResult
            {
                Summary = discoverySuggestions.Count > 0
                    ? $"Discovery mode generated {discoverySuggestions.Count} suggestion(s)."
                    : "No reliable detection rule could be suggested automatically. Please configure detection manually.",
                Suggestions = discoverySuggestions,
                RecommendedPrimaryDetection = discoverySuggestions.FirstOrDefault(),
                RecommendedSecondaryDetection = discoverySuggestions.Skip(1).FirstOrDefault()
            };

            DetectionSuggestionSummaryTextBlock.Text = _lastDetectionSuggestionResult.Summary;
            if (discoverySuggestions.Count > 0)
            {
                DetectionSuggestionsListBox.SelectedIndex = 0;
            }

            DetectionTestResultTextBox.Text =
                $"Discovery mode install exit code: {exitCode}{Environment.NewLine}" +
                "Note: discovery results are best-effort only. Review suggestions before applying.";

            SetStatus("Discovery mode complete. Review suggested detection rules.");
            _logger.Log($"{app.Name}: discovery mode completed with {discoverySuggestions.Count} suggestions.");
        }
        catch (Exception ex)
        {
            DetectionTestResultTextBox.Text = $"Discovery mode failed: {ex.Message}";
            SetStatus("Discovery mode failed.");
            _logger.Log($"{app.Name}: discovery mode failed - {ex.Message}");
        }
    }

    private void DetectionSuggestionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetectionSuggestionsListBox.SelectedItem is DetectionRuleSuggestion suggestion)
        {
            DetectionSuggestionSummaryTextBlock.Text =
                $"Selected ({suggestion.Confidence}): {suggestion.Reason}";
            DetectionExplanationTextBox.Text = _detectionService.ExplainRule(new DetectionRule
            {
                Type = suggestion.DetectionType,
                Value = suggestion.DetectionValue
            });
        }
    }

    private async Task BrowseAndImportInstallerAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select installer",
            Filter = "Installers (*.exe;*.msi)|*.exe;*.msi|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            await ImportInstallerFileAsync(dialog.FileName);
        }
    }

    private void GenerateDetectionSuggestions(bool autoTriggered)
    {
        try
        {
            var tempAppEntry = BuildSuggestionContextEntry();
            var result = _detectionSuggestionService.Suggest(
                tempAppEntry,
                tempAppEntry.InstallerPath,
                ProbeOutputTextBox.Text);

            _lastDetectionSuggestionResult = result;
            DetectionSuggestionsListBox.ItemsSource = result.Suggestions;
            var recommendationText = string.Empty;
            if (result.RecommendedPrimaryDetection is not null)
            {
                recommendationText += $"Recommended primary: {result.RecommendedPrimaryDetection.DisplayText}";
            }

            if (result.RecommendedSecondaryDetection is not null)
            {
                recommendationText +=
                    $"{Environment.NewLine}Recommended secondary: {result.RecommendedSecondaryDetection.DisplayText}";
            }

            DetectionSuggestionSummaryTextBlock.Text = string.IsNullOrWhiteSpace(recommendationText)
                ? result.Summary
                : $"{result.Summary}{Environment.NewLine}{recommendationText}";

            if (result.Suggestions.Count > 0)
            {
                DetectionSuggestionsListBox.SelectedIndex = 0;
                SetStatus(autoTriggered
                    ? "Detection suggestions refreshed from imported/app context."
                    : "Detection suggestions generated. Review and apply as needed.");
            }
            else
            {
                SetStatus("No reliable detection suggestion found. Configure detection manually.");
                DetectionTestResultTextBox.Text =
                    "No reliable detection rule could be suggested automatically. Please configure detection manually.";
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Detection suggestion helper failed: {ex.Message}");
            DetectionSuggestionsListBox.ItemsSource = null;
            DetectionSuggestionSummaryTextBlock.Text =
                "No reliable detection rule could be suggested automatically. Please configure detection manually.";
            SetStatus("Detection suggestion helper failed.");
        }

        UpdateDetectionExplanation();
    }

    private AppEntry BuildSuggestionContextEntry()
    {
        var sourceType = InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType selectedSource
            ? selectedSource
            : InstallerSourceType.FileServer;
        var primaryDetectionType = DetectionTypeComboBox.SelectedItem is DetectionType selectedDetection
            ? selectedDetection
            : DetectionType.RegistryDisplayName;
        var secondaryDetectionType = SecondaryDetectionTypeComboBox.SelectedItem is DetectionType secondaryType
            ? secondaryType
            : DetectionType.FileExists;

        DetectionRule? secondaryDetection = null;
        if (!string.IsNullOrWhiteSpace(SecondaryDetectionValueTextBox.Text))
        {
            secondaryDetection = new DetectionRule
            {
                Type = secondaryDetectionType,
                Value = SecondaryDetectionValueTextBox.Text.Trim()
            };
        }

        return new AppEntry
        {
            Name = NameTextBox.Text.Trim(),
            Description = DescriptionTextBox.Text.Trim(),
            Category = string.IsNullOrWhiteSpace(CategoryTextBox.Text) ? "General" : CategoryTextBox.Text.Trim(),
            Enabled = EnabledCheckBox.IsChecked ?? true,
            InstallerSourceType = sourceType,
            InstallerPath = InstallerPathTextBox.Text.Trim(),
            SilentArguments = SilentArgumentsTextBox.Text.Trim(),
            WingetId = WingetIdTextBox.Text.Trim(),
            WingetArguments = WingetArgumentsTextBox.Text.Trim(),
            PrimaryDetection = new DetectionRule
            {
                Type = primaryDetectionType,
                Value = DetectionValueTextBox.Text.Trim()
            },
            SecondaryDetection = secondaryDetection,
            IconPath = IconPathTextBox.Text.Trim(),
            RequiresAdmin = RequiresAdminCheckBox.IsChecked ?? false
        };
    }

    private async Task ImportInstallerFileAsync(string sourceInstallerPath)
    {
        if (!File.Exists(sourceInstallerPath))
        {
            ShowValidation("Selected installer file does not exist.");
            return;
        }

        var extension = Path.GetExtension(sourceInstallerPath).ToLowerInvariant();
        if (extension != ".exe" && extension != ".msi")
        {
            ShowValidation("Unsupported installer type. Supported types: .exe, .msi");
            return;
        }

        try
        {
            AppPaths.EnsureFileServerStructure(_logger);

            var appName = NameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(appName))
            {
                appName = Path.GetFileNameWithoutExtension(sourceInstallerPath);
                NameTextBox.Text = appName;
            }

            if (string.IsNullOrWhiteSpace(CategoryTextBox.Text))
            {
                CategoryTextBox.Text = "General";
            }

            var repositoryFolder = Path.Combine(
                AppPaths.FileServerRepositoryDirectory,
                AppPaths.SanitizePathSegment(appName, "NewApp"));
            Directory.CreateDirectory(repositoryFolder);

            var targetInstallerPath = Path.Combine(repositoryFolder, $"installer{extension}");
            _logger.Log($"Import start: '{sourceInstallerPath}' -> '{targetInstallerPath}'.");
            await Task.Run(() => File.Copy(sourceInstallerPath, targetInstallerPath, overwrite: true));
            _logger.Log($"Import copy success: '{targetInstallerPath}'.");

            _lastImportedRepositoryFolder = repositoryFolder;
            InstallerSourceTypeComboBox.SelectedItem = InstallerSourceType.FileServer;
            InstallerPathTextBox.Text = targetInstallerPath;
            WingetIdTextBox.Text = string.Empty;
            WingetArgumentsTextBox.Text = AppConfigService.DefaultWingetArguments;

            if (extension == ".msi")
            {
                SilentArgumentsTextBox.Text = "/qn /norestart";
            }

            ProbeSummaryTextBlock.Text = "Installer imported. You can now run silent switch detection.";
            SetStatus("Installer imported to repository.");
            MarkDirty(true);
            UpdateSourceTypeUi();
            UpdateCommandPreview();
            GenerateDetectionSuggestions(autoTriggered: true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Import copy failure: {ex.Message}");
            ShowValidation($"Failed to import installer: {ex.Message}");
        }
    }

    private bool FilterApps(object obj)
    {
        if (obj is not AppEntry app)
        {
            return false;
        }

        var term = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        return app.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               app.Category.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadConfigFromDisk()
    {
        _isLoadingState = true;

        try
        {
            AppPaths.EnsureFileServerStructure(_logger);
            var result = _configService.LoadOrCreateConfig(
                AppPaths.FileServerConfigFilePath,
                AppPaths.FileServerRepositoryDirectory);

            _apps.Clear();
            foreach (var app in result.Config.Apps.Select(Clone))
            {
                _apps.Add(app);
            }

            ConfigVersionTextBox.Text = result.Config.ConfigVersion;
            _appsView.Refresh();

            if (_apps.Count > 0)
            {
                AppsDataGrid.SelectedIndex = 0;
            }
            else
            {
                ClearEditor();
            }

            foreach (var error in result.Errors)
            {
                _logger.Log(error);
            }

            SetStatus($"Config loaded. {_apps.Count} app(s) available.");
        }
        catch (Exception ex)
        {
            _logger.Log($"Load config failed: {ex.Message}");
            MessageBox.Show(
                $"Could not load config at:\n{AppPaths.FileServerConfigFilePath}\n\n{ex.Message}",
                "Load Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetStatus("Config load failed.");
        }
        finally
        {
            MarkDirty(false);
            _isLoadingState = false;
            UpdateSourceTypeUi();
            UpdateCommandPreview();
        }
    }

    private List<string> ValidateAll()
    {
        var config = new AppCatalogueConfig
        {
            ConfigVersion = ConfigVersionTextBox.Text.Trim(),
            Apps = _apps.Select(Clone).ToList()
        };

        return AppValidator.ValidateConfig(config);
    }

    private bool TryBuildAppFromForm(out AppEntry app, out string error)
    {
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            app = new AppEntry();
            error = "Name is required.";
            return false;
        }

        var category = CategoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            app = new AppEntry();
            error = "Category is required.";
            return false;
        }

        if (InstallerSourceTypeComboBox.SelectedItem is not InstallerSourceType sourceType)
        {
            app = new AppEntry();
            error = "InstallerSourceType is required.";
            return false;
        }

        if (DetectionTypeComboBox.SelectedItem is not DetectionType detectionType)
        {
            app = new AppEntry();
            error = "PrimaryDetection.Type is required.";
            return false;
        }

        var detectionValue = DetectionValueTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(detectionValue))
        {
            app = new AppEntry();
            error = "PrimaryDetection.Value is required.";
            return false;
        }

        var secondaryDetectionType = SecondaryDetectionTypeComboBox.SelectedItem is DetectionType selectedSecondaryType
            ? selectedSecondaryType
            : DetectionType.FileExists;
        var secondaryDetectionValue = SecondaryDetectionValueTextBox.Text.Trim();

        DetectionRule? secondaryDetection = null;
        if (!string.IsNullOrWhiteSpace(secondaryDetectionValue))
        {
            secondaryDetection = new DetectionRule
            {
                Type = secondaryDetectionType,
                Value = secondaryDetectionValue
            };
        }

        var installerPath = InstallerPathTextBox.Text.Trim();
        var wingetId = WingetIdTextBox.Text.Trim();
        if (sourceType == InstallerSourceType.FileServer && string.IsNullOrWhiteSpace(installerPath))
        {
            app = new AppEntry();
            error = "InstallerPath is required for FileServer apps.";
            return false;
        }

        if (sourceType == InstallerSourceType.Winget && string.IsNullOrWhiteSpace(wingetId))
        {
            app = new AppEntry();
            error = "WingetId is required for Winget apps.";
            return false;
        }

        var wingetArguments = WingetArgumentsTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(wingetArguments))
        {
            wingetArguments = AppConfigService.DefaultWingetArguments;
        }

        app = new AppEntry
        {
            Name = name,
            Description = DescriptionTextBox.Text.Trim(),
            Category = category,
            Enabled = EnabledCheckBox.IsChecked ?? true,
            InstallerSourceType = sourceType,
            InstallerPath = installerPath,
            SilentArguments = SilentArgumentsTextBox.Text.Trim(),
            WingetId = wingetId,
            WingetArguments = wingetArguments,
            PrimaryDetection = new DetectionRule
            {
                Type = detectionType,
                Value = detectionValue
            },
            SecondaryDetection = secondaryDetection,
            IconPath = IconPathTextBox.Text.Trim(),
            RequiresAdmin = RequiresAdminCheckBox.IsChecked ?? false
        };

        if (!AppValidator.TryValidate(app, out error))
        {
            return false;
        }

        return true;
    }

    private void PopulateEditor(AppEntry app)
    {
        _isLoadingState = true;

        NameTextBox.Text = app.Name;
        DescriptionTextBox.Text = app.Description;
        CategoryTextBox.Text = app.Category;
        EnabledCheckBox.IsChecked = app.Enabled;
        InstallerSourceTypeComboBox.SelectedItem = app.InstallerSourceType;
        InstallerPathTextBox.Text = app.InstallerPath;
        SilentArgumentsTextBox.Text = app.SilentArguments;
        WingetIdTextBox.Text = app.WingetId;
        WingetArgumentsTextBox.Text = string.IsNullOrWhiteSpace(app.WingetArguments)
            ? AppConfigService.DefaultWingetArguments
            : app.WingetArguments;
        DetectionTypeComboBox.SelectedItem = app.PrimaryDetection.Type;
        DetectionValueTextBox.Text = app.PrimaryDetection.Value;
        SecondaryDetectionTypeComboBox.SelectedItem = app.SecondaryDetection?.Type ?? DetectionType.FileExists;
        SecondaryDetectionValueTextBox.Text = app.SecondaryDetection?.Value ?? string.Empty;
        IconPathTextBox.Text = app.IconPath;
        RequiresAdminCheckBox.IsChecked = app.RequiresAdmin;
        DetectionSuggestionsListBox.ItemsSource = null;
        DetectionSuggestionSummaryTextBlock.Text = string.Empty;
        DetectionExplanationTextBox.Text = string.Empty;
        DetectionTestResultTextBox.Text = string.Empty;
        _lastDetectionSuggestionResult = null;

        _lastImportedRepositoryFolder = ResolveRepositoryFolderForCurrentApp();
        _isLoadingState = false;
        UpdateSourceTypeUi();
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private void ClearEditor()
    {
        _isLoadingState = true;

        NameTextBox.Text = string.Empty;
        DescriptionTextBox.Text = string.Empty;
        CategoryTextBox.Text = "General";
        EnabledCheckBox.IsChecked = true;
        InstallerSourceTypeComboBox.SelectedItem = InstallerSourceType.FileServer;
        InstallerPathTextBox.Text = string.Empty;
        SilentArgumentsTextBox.Text = string.Empty;
        WingetIdTextBox.Text = string.Empty;
        WingetArgumentsTextBox.Text = AppConfigService.DefaultWingetArguments;
        DetectionTypeComboBox.SelectedItem = DetectionType.RegistryDisplayName;
        DetectionValueTextBox.Text = string.Empty;
        SecondaryDetectionTypeComboBox.SelectedItem = DetectionType.FileExists;
        SecondaryDetectionValueTextBox.Text = string.Empty;
        IconPathTextBox.Text = string.Empty;
        RequiresAdminCheckBox.IsChecked = false;
        ProbeSummaryTextBlock.Text = string.Empty;
        ProbeOutputTextBox.Text = string.Empty;
        SuggestionComboBox.ItemsSource = null;
        DetectionSuggestionsListBox.ItemsSource = null;
        DetectionSuggestionSummaryTextBlock.Text = string.Empty;
        DetectionExplanationTextBox.Text = string.Empty;
        DetectionTestResultTextBox.Text = string.Empty;
        _lastDetectionSuggestionResult = null;
        _lastImportedRepositoryFolder = string.Empty;

        _isLoadingState = false;
        UpdateSourceTypeUi();
        UpdateCommandPreview();
        UpdateDetectionExplanation();
    }

    private void UpdateSourceTypeUi()
    {
        var isWinget = InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType source &&
                       source == InstallerSourceType.Winget;

        InstallerPathTextBox.IsEnabled = !isWinget;
        SilentArgumentsTextBox.IsEnabled = !isWinget;
        BrowseInstallerPathButton.IsEnabled = !isWinget;
        ImportInstallerButton.IsEnabled = !isWinget;
        DetectSilentSwitchesButton.IsEnabled = !isWinget;
        DiscoveryModeButton.IsEnabled = !isWinget;
        ImportDropZone.IsEnabled = !isWinget;
        ImportDropZone.Opacity = isWinget ? 0.55 : 1.0;

        WingetIdTextBox.IsEnabled = isWinget;
        WingetArgumentsTextBox.IsEnabled = isWinget;
    }

    private void UpdateCommandPreview()
    {
        var sourceType = InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType source
            ? source
            : InstallerSourceType.FileServer;

        string preview;
        if (sourceType == InstallerSourceType.Winget)
        {
            var wingetId = string.IsNullOrWhiteSpace(WingetIdTextBox.Text)
                ? "<WingetId>"
                : WingetIdTextBox.Text.Trim();
            var wingetArgs = string.IsNullOrWhiteSpace(WingetArgumentsTextBox.Text)
                ? AppConfigService.DefaultWingetArguments
                : WingetArgumentsTextBox.Text.Trim();

            preview = $"winget install --id \"{wingetId}\" -e {wingetArgs}";
        }
        else
        {
            var installerPath = string.IsNullOrWhiteSpace(InstallerPathTextBox.Text)
                ? "<InstallerPath>"
                : InstallerPathTextBox.Text.Trim();
            var silentArguments = SilentArgumentsTextBox.Text.Trim();

            if (Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(silentArguments))
                {
                    silentArguments = "/qn /norestart";
                }

                preview = $"msiexec /i \"{installerPath}\" {silentArguments}";
            }
            else
            {
                preview = $"\"{installerPath}\" {silentArguments}".Trim();
            }
        }

        CommandPreviewTextBox.Text = preview;
    }

    private void UpdateDetectionExplanation()
    {
        var primaryRule = new DetectionRule
        {
            Type = DetectionTypeComboBox.SelectedItem is DetectionType primaryType
                ? primaryType
                : DetectionType.RegistryDisplayName,
            Value = DetectionValueTextBox.Text.Trim()
        };

        DetectionRule? secondaryRule = null;
        if (!string.IsNullOrWhiteSpace(SecondaryDetectionValueTextBox.Text))
        {
            secondaryRule = new DetectionRule
            {
                Type = SecondaryDetectionTypeComboBox.SelectedItem is DetectionType secondaryType
                    ? secondaryType
                    : DetectionType.FileExists,
                Value = SecondaryDetectionValueTextBox.Text.Trim()
            };
        }

        var explanation =
            "Primary Detection" + Environment.NewLine +
            _detectionService.ExplainRule(primaryRule);

        if (secondaryRule is not null)
        {
            explanation += Environment.NewLine + Environment.NewLine +
                           "Secondary Detection" + Environment.NewLine +
                           _detectionService.ExplainRule(secondaryRule);
        }

        DetectionExplanationTextBox.Text = explanation;
    }

    private string BuildDetectionTestOutput(DetectionEvaluationResult result)
    {
        var lines = new List<string>
        {
            $"Overall Result: {(result.IsInstalled ? "PASS" : "FAIL")}",
            result.Summary,
            string.Empty
        };

        foreach (var rule in result.RuleResults)
        {
            lines.Add($"{rule.RuleName}: {(rule.Passed ? "PASS" : "FAIL")}");
            lines.Add($"Type: {rule.DetectionType}");
            lines.Add($"Value: {rule.DetectionValue}");
            if (!string.IsNullOrWhiteSpace(rule.MatchLocation))
            {
                lines.Add($"Match Location: {rule.MatchLocation}");
            }

            lines.Add($"Detail: {rule.Detail}");
            if (rule.CheckedLocations.Count > 0)
            {
                lines.Add("Checked Locations:");
                lines.AddRange(rule.CheckedLocations.Select(location => $"  - {location}"));
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<int> RunInstallerForDiscoveryAsync(string installerPath, string silentArguments)
    {
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();
        ProcessStartInfo startInfo;
        if (extension == ".msi")
        {
            var msiArgs = string.IsNullOrWhiteSpace(silentArguments) ? "/qn /norestart" : silentArguments.Trim();
            startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{installerPath}\" {msiArgs}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = string.IsNullOrWhiteSpace(silentArguments) ? string.Empty : silentArguments.Trim(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start installer for discovery mode.");
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private HashSet<string> CaptureFilePathSnapshot(string appName)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var tokens = appName
            .Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Select(token => token.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (tokens.Count == 0 && !string.IsNullOrWhiteSpace(appName))
        {
            tokens.Add(appName.Trim().ToLowerInvariant());
        }

        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            CaptureCandidatesRecursive(root, depth: 0, maxDepth: 3, tokens, snapshot);
        }

        return snapshot;
    }

    private static void CaptureCandidatesRecursive(
        string directoryPath,
        int depth,
        int maxDepth,
        List<string> tokens,
        HashSet<string> snapshot)
    {
        if (depth > maxDepth)
        {
            return;
        }

        IEnumerable<string> subDirectories;
        try
        {
            subDirectories = Directory.EnumerateDirectories(directoryPath);
        }
        catch
        {
            return;
        }

        foreach (var subDirectory in subDirectories)
        {
            var normalized = subDirectory.ToLowerInvariant();
            if (tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                snapshot.Add(subDirectory);

                try
                {
                    foreach (var exePath in Directory.EnumerateFiles(subDirectory, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        snapshot.Add(exePath);
                    }
                }
                catch
                {
                    // Ignore access issues; this is best-effort discovery only.
                }
            }

            CaptureCandidatesRecursive(subDirectory, depth + 1, maxDepth, tokens, snapshot);
        }
    }

    private List<DetectionRuleSuggestion> BuildDiscoverySuggestions(
        string appName,
        List<UninstallRegistryEntry> beforeUninstallEntries,
        List<UninstallRegistryEntry> afterUninstallEntries,
        HashSet<string> beforePaths,
        HashSet<string> afterPaths)
    {
        var suggestions = new List<DetectionRuleSuggestion>();

        var newUninstallEntries = afterUninstallEntries
            .Where(after => beforeUninstallEntries.All(before =>
                !string.Equals(before.RegistryPath, after.RegistryPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var entry in newUninstallEntries)
        {
            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = DetectionType.RegistryDisplayName,
                DetectionValue = entry.DisplayName,
                Confidence = "High",
                Reason = "New uninstall DisplayName detected after test install."
            });

            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = DetectionType.RegistryKeyExists,
                DetectionValue = entry.RegistryPath,
                Confidence = "Medium",
                Reason = "New uninstall registry key detected after test install."
            });
        }
        var newPaths = afterPaths
            .Where(path => !beforePaths.Contains(path))
            .Take(8)
            .ToList();

        foreach (var path in newPaths)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            suggestions.Add(new DetectionRuleSuggestion
            {
                DetectionType = DetectionType.FileExists,
                DetectionValue = path,
                Confidence = "Medium",
                Reason = "New executable path detected during discovery scan."
            });
        }

        return suggestions
            .GroupBy(
                suggestion => $"{suggestion.DetectionType}|{suggestion.DetectionValue}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(suggestion => suggestion.DetectionType == DetectionType.RegistryDisplayName ? 0 :
                                   suggestion.DetectionType == DetectionType.FileExists ? 1 : 2)
            .ThenByDescending(suggestion => SuggestionConfidenceScore(suggestion.Confidence))
            .ThenBy(suggestion => suggestion.DetectionValue, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int SuggestionConfidenceScore(string confidence)
    {
        return confidence.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            _ => 1
        };
    }

    private void HandleDragFeedback(DragEventArgs e)
    {
        if (InstallerSourceTypeComboBox.SelectedItem is InstallerSourceType sourceType &&
            sourceType == InstallerSourceType.Winget)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var hasValidInstaller = TryGetFirstValidInstallerPath(e, out _);
        e.Effects = hasValidInstaller ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetDropZoneHighlight(hasValidInstaller);
    }

    private static bool TryGetFirstValidInstallerPath(DragEventArgs e, out string installerPath)
    {
        installerPath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        var file = files[0];
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension != ".exe" && extension != ".msi")
        {
            return false;
        }

        installerPath = file;
        return true;
    }

    private void SetDropZoneHighlight(bool highlighted)
    {
        ImportDropZone.BorderBrush = highlighted ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromRgb(0x76, 0xA3, 0xD8));
        DropZoneTextBlock.Text = highlighted
            ? "Release to import installer into repository"
            : "Drag and drop .exe or .msi installer files here";
    }

    private string ResolveRepositoryFolderForCurrentApp()
    {
        if (!string.IsNullOrWhiteSpace(_lastImportedRepositoryFolder))
        {
            return _lastImportedRepositoryFolder;
        }

        var appName = NameTextBox.Text.Trim();
        return Path.Combine(
            AppPaths.FileServerRepositoryDirectory,
            AppPaths.SanitizePathSegment(string.IsNullOrWhiteSpace(appName) ? "NewApp" : appName));
    }

    private static void ApplyChanges(AppEntry target, AppEntry source)
    {
        target.Name = source.Name;
        target.Description = source.Description;
        target.Category = source.Category;
        target.Enabled = source.Enabled;
        target.InstallerSourceType = source.InstallerSourceType;
        target.InstallerPath = source.InstallerPath;
        target.SilentArguments = source.SilentArguments;
        target.WingetId = source.WingetId;
        target.WingetArguments = source.WingetArguments;
        target.PrimaryDetection = source.PrimaryDetection is null
            ? new DetectionRule { Type = DetectionType.RegistryDisplayName, Value = string.Empty }
            : new DetectionRule { Type = source.PrimaryDetection.Type, Value = source.PrimaryDetection.Value };
        target.SecondaryDetection = source.SecondaryDetection is null
            ? null
            : new DetectionRule { Type = source.SecondaryDetection.Type, Value = source.SecondaryDetection.Value };
        target.IconPath = source.IconPath;
        target.RequiresAdmin = source.RequiresAdmin;
    }

    private static AppEntry Clone(AppEntry source)
    {
        return new AppEntry
        {
            Name = source.Name,
            Description = source.Description,
            Category = source.Category,
            Enabled = source.Enabled,
            InstallerSourceType = source.InstallerSourceType,
            InstallerPath = source.InstallerPath,
            SilentArguments = source.SilentArguments,
            WingetId = source.WingetId,
            WingetArguments = source.WingetArguments,
            PrimaryDetection = source.PrimaryDetection is null
                ? new DetectionRule { Type = DetectionType.RegistryDisplayName, Value = string.Empty }
                : new DetectionRule { Type = source.PrimaryDetection.Type, Value = source.PrimaryDetection.Value },
            SecondaryDetection = source.SecondaryDetection is null
                ? null
                : new DetectionRule { Type = source.SecondaryDetection.Type, Value = source.SecondaryDetection.Value },
            IconPath = source.IconPath,
            RequiresAdmin = source.RequiresAdmin
        };
    }

    private string BuildDuplicateName(string sourceName)
    {
        var baseName = $"{sourceName} Copy";
        var candidate = baseName;
        var index = 2;

        while (_apps.Any(app => app.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {index}";
            index++;
        }

        return candidate;
    }

    private void ShowValidation(string message)
    {
        MessageBox.Show(message, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        SetStatus(message);
    }

    private void MarkDirty(bool dirty)
    {
        _isDirty = dirty;
        DirtyIndicatorTextBlock.Text = dirty ? "* Unsaved changes" : string.Empty;
        Title = dirty ? "AppCatalogueAdmin *" : "AppCatalogueAdmin";
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isDirty)
        {
            var confirm = MessageBox.Show(
                "You have unsaved changes. Close anyway?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }
}
