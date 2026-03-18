using System.Text;
using System.Text.Json;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class DiscoverySettingsService
{
    private readonly FileLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public DiscoverySettingsService(FileLogger logger)
    {
        _logger = logger;
    }

    public DiscoveryModeSettings LoadOrCreate(string filePath)
    {
        try
        {
            EnsureParentDirectory(filePath);
            if (!File.Exists(filePath))
            {
                var defaults = CreateDefaults();
                Save(filePath, defaults);
                _logger.Log($"Discovery settings created at '{filePath}'.");
                return defaults;
            }

            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<DiscoveryModeSettings>(json, _jsonOptions) ?? CreateDefaults();
            return Normalize(parsed);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to load discovery settings from '{filePath}': {ex.Message}");
            return CreateDefaults();
        }
    }

    public void Save(string filePath, DiscoveryModeSettings settings)
    {
        var normalized = Normalize(settings);
        EnsureParentDirectory(filePath);
        var json = JsonSerializer.Serialize(normalized, _jsonOptions);
        File.WriteAllText(filePath, json, Encoding.UTF8);
        _logger.Log($"Discovery settings saved at '{filePath}'.");
    }

    public static DiscoveryModeSettings CreateDefaults()
    {
        return new DiscoveryModeSettings
        {
            VmName = "AppCatalogueLab01",
            CheckpointName = "CleanState",
            GuestInputDirectory = @"C:\Discovery\Input",
            GuestOutputDirectory = @"C:\Discovery\Output",
            GuestScriptsDirectory = @"C:\Discovery\Scripts",
            HostStagingDirectory = AppPaths.DiscoveryHostStagingDirectory,
            HostResultsDirectory = AppPaths.DiscoveryResultsDirectory,
            GuestReadyTimeoutSeconds = 300,
            DiscoveryTimeoutSeconds = 1800,
            CommandTimeoutSeconds = 120,
            ProbeTimeoutSeconds = 15,
            InstallerTimeoutSeconds = 1200,
            ShutdownVmOnComplete = true
        };
    }

    private static DiscoveryModeSettings Normalize(DiscoveryModeSettings settings)
    {
        settings.VmName = string.IsNullOrWhiteSpace(settings.VmName) ? "AppCatalogueLab01" : settings.VmName.Trim();
        settings.CheckpointName = string.IsNullOrWhiteSpace(settings.CheckpointName) ? "CleanState" : settings.CheckpointName.Trim();
        settings.GuestInputDirectory = string.IsNullOrWhiteSpace(settings.GuestInputDirectory) ? @"C:\Discovery\Input" : settings.GuestInputDirectory.Trim();
        settings.GuestOutputDirectory = string.IsNullOrWhiteSpace(settings.GuestOutputDirectory) ? @"C:\Discovery\Output" : settings.GuestOutputDirectory.Trim();
        settings.GuestScriptsDirectory = string.IsNullOrWhiteSpace(settings.GuestScriptsDirectory) ? @"C:\Discovery\Scripts" : settings.GuestScriptsDirectory.Trim();
        settings.HostStagingDirectory = string.IsNullOrWhiteSpace(settings.HostStagingDirectory) ? AppPaths.DiscoveryHostStagingDirectory : settings.HostStagingDirectory.Trim();
        settings.HostResultsDirectory = string.IsNullOrWhiteSpace(settings.HostResultsDirectory) ? AppPaths.DiscoveryResultsDirectory : settings.HostResultsDirectory.Trim();
        settings.GuestReadyTimeoutSeconds = NormalizeTimeout(settings.GuestReadyTimeoutSeconds, 300);
        settings.DiscoveryTimeoutSeconds = NormalizeTimeout(settings.DiscoveryTimeoutSeconds, 1800);
        settings.CommandTimeoutSeconds = NormalizeTimeout(settings.CommandTimeoutSeconds, 120);
        settings.ProbeTimeoutSeconds = NormalizeTimeout(settings.ProbeTimeoutSeconds, 15);
        settings.InstallerTimeoutSeconds = NormalizeTimeout(settings.InstallerTimeoutSeconds, 1200);
        return settings;
    }

    private static int NormalizeTimeout(int value, int fallback)
    {
        return value <= 0 ? fallback : value;
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
