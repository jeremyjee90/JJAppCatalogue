using System.IO;

namespace AppCatalogue.Shared.Services;

public static class AppPaths
{
    public const string EndpointRootDirectory = @"C:\ProgramData\AppCatalogue";
    public const string EndpointConfigDirectory = @"C:\ProgramData\AppCatalogue\Config";
    public const string EndpointConfigFilePath = @"C:\ProgramData\AppCatalogue\Config\apps.json";
    public const string EndpointCacheDirectory = @"C:\ProgramData\AppCatalogue\Cache";
    public const string EndpointLogsDirectory = @"C:\ProgramData\AppCatalogue\Logs";
    public const string EndpointIconsDirectory = @"C:\ProgramData\AppCatalogue\Icons";
    public const string EndpointRequestsDirectory = @"C:\ProgramData\AppCatalogue\Requests";
    public const string EndpointDiscoveryRootDirectory = @"C:\ProgramData\AppCatalogue\Discovery";
    public const string EndpointDiscoveryJobsDirectory = @"C:\ProgramData\AppCatalogue\Discovery\Jobs";

    public const string FileServerRootDirectory = @"C:\Installers";
    public const string FileServerAdminDirectory = @"C:\Installers\Admin";
    public const string FileServerRepositoryDirectory = @"C:\Installers\Repository";
    public const string FileServerConfigDirectory = @"C:\Installers\Config";
    public const string FileServerConfigFilePath = @"C:\Installers\Config\apps.json";
    public const string FileServerScriptsDirectory = @"C:\Installers\Scripts";
    public const string FileServerLogsDirectory = @"C:\Installers\Logs";
    public const string DiscoveryRootDirectory = @"C:\Installers\Discovery";
    public const string DiscoveryHostStagingDirectory = @"C:\Installers\Discovery\HostStaging";
    public const string DiscoveryResultsDirectory = @"C:\Installers\Discovery\Results";
    public const string DiscoveryScriptsDirectory = @"C:\Installers\Discovery\Scripts";
    public const string DiscoverySettingsFilePath = @"C:\Installers\Config\discovery-settings.json";

    public static string EndUserLogPath => Path.Combine(EndpointLogsDirectory, "AppCatalogue.log");
    public static string AdminLogPath => Path.Combine(FileServerLogsDirectory, "AppCatalogueAdmin.log");

    public static void EnsureEndpointStructure()
    {
        Directory.CreateDirectory(EndpointRootDirectory);
        Directory.CreateDirectory(EndpointConfigDirectory);
        Directory.CreateDirectory(EndpointCacheDirectory);
        Directory.CreateDirectory(EndpointLogsDirectory);
        Directory.CreateDirectory(EndpointIconsDirectory);
        Directory.CreateDirectory(EndpointRequestsDirectory);
        Directory.CreateDirectory(EndpointDiscoveryRootDirectory);
        Directory.CreateDirectory(EndpointDiscoveryJobsDirectory);
    }

    public static void EnsureFileServerStructure(FileLogger? logger = null)
    {
        EnsureDirectory(FileServerRootDirectory, logger);
        EnsureDirectory(FileServerAdminDirectory, logger);
        EnsureDirectory(FileServerRepositoryDirectory, logger);
        EnsureDirectory(FileServerConfigDirectory, logger);
        EnsureDirectory(FileServerScriptsDirectory, logger);
        EnsureDirectory(FileServerLogsDirectory, logger);
        EnsureDirectory(DiscoveryRootDirectory, logger);
        EnsureDirectory(DiscoveryHostStagingDirectory, logger);
        EnsureDirectory(DiscoveryResultsDirectory, logger);
        EnsureDirectory(DiscoveryScriptsDirectory, logger);
    }

    public static void CleanupEndpointCache(FileLogger? logger = null)
    {
        EnsureEndpointStructure();

        try
        {
            foreach (var cachedFile in Directory.EnumerateFiles(EndpointCacheDirectory))
            {
                try
                {
                    File.Delete(cachedFile);
                    logger?.Log($"Cache cleanup removed file: {cachedFile}");
                }
                catch (Exception ex)
                {
                    logger?.Log($"Cache cleanup failed for '{cachedFile}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Log($"Cache cleanup failed: {ex.Message}");
        }
    }

    public static string SanitizePathSegment(string value, string fallbackValue = "App")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackValue;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = value.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(safeChars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? fallbackValue : safe;
    }

    private static void EnsureDirectory(string path, FileLogger? logger)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            logger?.Log($"Failed to ensure directory '{path}': {ex.Message}");
        }
    }
}
