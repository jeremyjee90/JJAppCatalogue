using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class AppConfigService
{
    public const string DefaultWingetArguments = "--silent --accept-package-agreements --accept-source-agreements";

    private readonly FileLogger _logger;
    private readonly JsonSerializerOptions _serializerOptions;

    public AppConfigService(FileLogger logger)
    {
        _logger = logger;
        _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public AppConfigLoadResult LoadOrCreateConfig(string configFilePath, string repositoryRootForDefaults)
    {
        try
        {
            EnsureParentDirectory(configFilePath);
        }
        catch (Exception ex)
        {
            var fallback = CreateDefaultConfig(repositoryRootForDefaults);
            var message = $"Failed to access config directory for '{configFilePath}': {ex.Message}";
            _logger.Log(message);
            return new AppConfigLoadResult
            {
                Config = fallback,
                Errors = [message]
            };
        }

        if (!File.Exists(configFilePath))
        {
            var defaultConfig = CreateDefaultConfig(repositoryRootForDefaults);
            try
            {
                SaveConfig(defaultConfig, configFilePath);
                _logger.Log($"Created default config at {configFilePath}.");
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to create default config: {ex.Message}");
            }
        }

        return LoadConfig(configFilePath, repositoryRootForDefaults);
    }

    public AppConfigLoadResult LoadConfig(string configFilePath, string repositoryRootForDefaults)
    {
        var errors = new List<string>();

        try
        {
            EnsureParentDirectory(configFilePath);
            var json = File.ReadAllText(configFilePath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<AppCatalogueConfig>(json, _serializerOptions) ?? new AppCatalogueConfig();

            var normalized = new AppCatalogueConfig
            {
                ConfigVersion = string.IsNullOrWhiteSpace(parsed.ConfigVersion) ? "1.0.0" : parsed.ConfigVersion.Trim(),
                Apps = []
            };

            foreach (var app in parsed.Apps ?? [])
            {
                var candidate = Normalize(app);
                if (AppValidator.TryValidate(candidate, out var validationError))
                {
                    normalized.Apps.Add(candidate);
                }
                else
                {
                    var message = $"Skipped invalid app entry '{candidate.Name}': {validationError}";
                    errors.Add(message);
                    _logger.Log(message);
                }
            }

            if (normalized.Apps.Count == 0)
            {
                errors.Add("No valid app entries found. Loading defaults.");
                _logger.Log("No valid app entries found in config. Loading defaults.");
                normalized = CreateDefaultConfig(repositoryRootForDefaults);
            }

            return new AppConfigLoadResult
            {
                Config = normalized,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            var fallback = CreateDefaultConfig(repositoryRootForDefaults);
            var message = $"Failed to load config '{configFilePath}': {ex.Message}";
            errors.Add(message);
            _logger.Log(message);

            return new AppConfigLoadResult
            {
                Config = fallback,
                Errors = errors
            };
        }
    }

    public void SaveConfig(AppCatalogueConfig config, string configFilePath)
    {
        EnsureParentDirectory(configFilePath);

        var normalized = new AppCatalogueConfig
        {
            ConfigVersion = string.IsNullOrWhiteSpace(config.ConfigVersion) ? "1.0.0" : config.ConfigVersion.Trim(),
            Apps = []
        };

        foreach (var app in config.Apps ?? [])
        {
            var candidate = Normalize(app);
            if (!AppValidator.TryValidate(candidate, out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            normalized.Apps.Add(candidate);
        }

        if (normalized.Apps.Count == 0)
        {
            throw new InvalidOperationException("At least one valid app entry is required.");
        }

        var validationErrors = AppValidator.ValidateConfig(normalized);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        var json = JsonSerializer.Serialize(normalized, _serializerOptions);
        File.WriteAllText(configFilePath, json, Encoding.UTF8);
        _logger.Log($"Saved config with {normalized.Apps.Count} apps to {configFilePath}.");
    }

    public AppCatalogueConfig CreateDefaultConfig(string repositoryRootPath = AppPaths.FileServerRepositoryDirectory)
    {
        return new AppCatalogueConfig
        {
            ConfigVersion = "1.0.0",
            Apps =
            [
                new AppEntry
                {
                    Name = "Google Chrome",
                    Description = "Fast and secure web browser.",
                    Category = "Browsers",
                    Enabled = true,
                    InstallerSourceType = InstallerSourceType.FileServer,
                    InstallerPath = Path.Combine(repositoryRootPath, "Google Chrome", "installer.exe"),
                    SilentArguments = "/silent /install",
                    WingetId = string.Empty,
                    WingetArguments = DefaultWingetArguments,
                    PrimaryDetection = new DetectionRule
                    {
                        Type = DetectionType.RegistryDisplayName,
                        Value = "Google Chrome"
                    },
                    SecondaryDetection = new DetectionRule
                    {
                        Type = DetectionType.FileExists,
                        Value = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe"
                    },
                    IconPath = Path.Combine(repositoryRootPath, "Google Chrome", "chrome.png"),
                    RequiresAdmin = true
                },
                new AppEntry
                {
                    Name = "Mozilla Firefox",
                    Description = "Open-source browser with privacy controls.",
                    Category = "Browsers",
                    Enabled = true,
                    InstallerSourceType = InstallerSourceType.FileServer,
                    InstallerPath = Path.Combine(repositoryRootPath, "Mozilla Firefox", "installer.exe"),
                    SilentArguments = "/S",
                    WingetId = string.Empty,
                    WingetArguments = DefaultWingetArguments,
                    PrimaryDetection = new DetectionRule
                    {
                        Type = DetectionType.RegistryDisplayName,
                        Value = "Mozilla Firefox"
                    },
                    SecondaryDetection = new DetectionRule
                    {
                        Type = DetectionType.FileExists,
                        Value = @"%ProgramFiles%\Mozilla Firefox\firefox.exe"
                    },
                    IconPath = Path.Combine(repositoryRootPath, "Mozilla Firefox", "firefox.png"),
                    RequiresAdmin = true
                },
                new AppEntry
                {
                    Name = "Discord",
                    Description = "Voice and chat platform for teams.",
                    Category = "Communication",
                    Enabled = true,
                    InstallerSourceType = InstallerSourceType.FileServer,
                    InstallerPath = Path.Combine(repositoryRootPath, "Discord", "installer.exe"),
                    SilentArguments = "/S",
                    WingetId = string.Empty,
                    WingetArguments = DefaultWingetArguments,
                    PrimaryDetection = new DetectionRule
                    {
                        Type = DetectionType.RegistryDisplayName,
                        Value = "Discord"
                    },
                    SecondaryDetection = new DetectionRule
                    {
                        Type = DetectionType.FileExists,
                        Value = @"%LocalAppData%\Discord\Update.exe"
                    },
                    IconPath = Path.Combine(repositoryRootPath, "Discord", "discord.png"),
                    RequiresAdmin = false
                }
            ]
        };
    }

    private static AppEntry Normalize(AppEntry app)
    {
        var sourceType = app.InstallerSourceType;
        var normalizedWingetArguments = string.IsNullOrWhiteSpace(app.WingetArguments)
            ? DefaultWingetArguments
            : app.WingetArguments.Trim();
        var normalizedPrimary = NormalizeDetectionRule(app.PrimaryDetection);
        if ((normalizedPrimary is null || string.IsNullOrWhiteSpace(normalizedPrimary.Value)) &&
            app.LegacyDetectionType.HasValue &&
            !string.IsNullOrWhiteSpace(app.LegacyDetectionValue))
        {
            normalizedPrimary = new DetectionRule
            {
                Type = app.LegacyDetectionType.Value,
                Value = app.LegacyDetectionValue.Trim()
            };
        }

        normalizedPrimary ??= new DetectionRule
        {
            Type = DetectionType.RegistryDisplayName,
            Value = string.Empty
        };

        var normalizedSecondary = NormalizeDetectionRule(app.SecondaryDetection);
        if (normalizedSecondary is not null && string.IsNullOrWhiteSpace(normalizedSecondary.Value))
        {
            normalizedSecondary = null;
        }

        return new AppEntry
        {
            Name = (app.Name ?? string.Empty).Trim(),
            Description = (app.Description ?? string.Empty).Trim(),
            Category = string.IsNullOrWhiteSpace(app.Category) ? "General" : app.Category.Trim(),
            Enabled = app.Enabled,
            InstallerSourceType = sourceType,
            InstallerPath = (app.InstallerPath ?? string.Empty).Trim(),
            SilentArguments = (app.SilentArguments ?? string.Empty).Trim(),
            WingetId = (app.WingetId ?? string.Empty).Trim(),
            WingetArguments = normalizedWingetArguments,
            PrimaryDetection = normalizedPrimary,
            SecondaryDetection = normalizedSecondary,
            LegacyDetectionType = null,
            LegacyDetectionValue = null,
            IconPath = (app.IconPath ?? string.Empty).Trim(),
            RequiresAdmin = app.RequiresAdmin
        };
    }

    private static DetectionRule? NormalizeDetectionRule(DetectionRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        return new DetectionRule
        {
            Type = rule.Type,
            Value = (rule.Value ?? string.Empty).Trim()
        };
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
