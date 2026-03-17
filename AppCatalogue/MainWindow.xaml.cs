using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AppCatalogue.Shared.Models;
using AppCatalogue.Shared.Services;
using AppCatalogue.ViewModels;

namespace AppCatalogue;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FileLogger _logger;
    private readonly AppConfigService _configService;
    private readonly AppDetectionService _detectionService;
    private readonly AppActionService _actionService;
    private string _configVersionText = string.Empty;
    private string _statusMessage = "Ready";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        AppPaths.EnsureEndpointStructure();
        _logger = new FileLogger(AppPaths.EndUserLogPath);
        _configService = new AppConfigService(_logger);
        _detectionService = new AppDetectionService();
        _actionService = new AppActionService(_logger);
        AppPaths.CleanupEndpointCache(_logger);
    }

    public ObservableCollection<AppCardViewModel> Apps { get; } = [];

    public string ConfigVersionText
    {
        get => _configVersionText;
        set
        {
            if (_configVersionText == value)
            {
                return;
            }

            _configVersionText = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAppsAsync();
    }

    private async void ReloadAppsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAppsAsync();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppCardViewModel app })
        {
            return;
        }

        if (app.IsBusy)
        {
            return;
        }

        app.IsBusy = true;
        app.Status = "Installing";
        StatusMessage = $"{app.Name}: starting installation...";

        try
        {
            var result = await _actionService.ExecuteAsync(app.Entry);
            if (!result.Success)
            {
                app.Status = string.IsNullOrWhiteSpace(result.Status) ? "Failed" : result.Status;
                StatusMessage = $"{app.Name}: {result.Message}";
                return;
            }

            if (string.Equals(result.Status, "Requested", StringComparison.OrdinalIgnoreCase))
            {
                app.Status = "Requested";
                StatusMessage = $"{app.Name}: request created.";
                return;
            }

            var installed = await Task.Run(() => _detectionService.IsInstalled(app.Entry));
            app.Status = DetermineBaseStatus(app.Entry, installed);
            StatusMessage = $"{app.Name}: {result.Message}";
        }
        catch (Exception ex)
        {
            _logger.Log($"{app.Name}: unexpected action error - {ex.Message}");
            app.Status = "Failed";
            StatusMessage = $"{app.Name}: failed";
        }
        finally
        {
            app.IsBusy = false;
        }
    }

    private async Task LoadAppsAsync()
    {
        StatusMessage = "Loading local catalogue...";

        var loadResult = await Task.Run(() =>
            _configService.LoadOrCreateConfig(
                AppPaths.EndpointConfigFilePath,
                AppPaths.FileServerRepositoryDirectory));
        foreach (var error in loadResult.Errors)
        {
            _logger.Log(error);
        }

        var enabledApps = loadResult.Config.Apps
            .Where(app => app.Enabled)
            .OrderBy(app => app.Category)
            .ThenBy(app => app.Name)
            .ToList();

        Apps.Clear();
        foreach (var app in enabledApps)
        {
            Apps.Add(new AppCardViewModel
            {
                Entry = app,
                Icon = LoadIcon(app.IconPath)
            });
        }

        ConfigVersionText =
            $"Config Version: {loadResult.Config.ConfigVersion}   |   Config: {AppPaths.EndpointConfigFilePath}";

        await RefreshStatusesAsync();
        if (loadResult.Errors.Count > 0)
        {
            StatusMessage = $"Loaded {Apps.Count} app(s). {loadResult.Errors.Count} invalid entry(ies) were skipped.";
        }
        else
        {
            StatusMessage = $"Loaded {Apps.Count} app(s).";
        }
    }

    private async Task RefreshStatusesAsync()
    {
        foreach (var app in Apps)
        {
            if (app.IsBusy)
            {
                continue;
            }

            var installed = await Task.Run(() => _detectionService.IsInstalled(app.Entry));
            app.Status = DetermineBaseStatus(app.Entry, installed);
        }
    }

    private static string DetermineBaseStatus(AppEntry entry, bool installed)
    {
        if (installed)
        {
            return "Installed";
        }

        if (entry.InstallerSourceType == InstallerSourceType.FileServer)
        {
            var installerPath = Environment.ExpandEnvironmentVariables(entry.InstallerPath.Trim());
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                return "Installer Missing";
            }
        }

        return "Ready";
    }

    private BitmapImage? LoadIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return null;
        }

        try
        {
            var resolvedPath = Environment.ExpandEnvironmentVariables(iconPath.Trim());
            if (!File.Exists(resolvedPath))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(resolvedPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            _logger.Log($"Icon load failed for '{iconPath}': {ex.Message}");
            return null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
