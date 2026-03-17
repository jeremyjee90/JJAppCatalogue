using System.IO;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public static class AppValidator
{
    public static bool TryValidate(AppEntry app, out string error)
    {
        if (string.IsNullOrWhiteSpace(app.Name))
        {
            error = "Name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(app.Category))
        {
            error = $"{app.Name}: Category is required.";
            return false;
        }

        if (!Enum.IsDefined(app.InstallerSourceType))
        {
            error = $"{app.Name}: InstallerSourceType is invalid.";
            return false;
        }

        if (app.InstallerSourceType == InstallerSourceType.FileServer && string.IsNullOrWhiteSpace(app.InstallerPath))
        {
            error = $"{app.Name}: InstallerPath is required for FileServer apps.";
            return false;
        }

        if (app.InstallerSourceType == InstallerSourceType.Winget && string.IsNullOrWhiteSpace(app.WingetId))
        {
            error = $"{app.Name}: WingetId is required for Winget apps.";
            return false;
        }

        if (app.PrimaryDetection is null)
        {
            error = $"{app.Name}: PrimaryDetection is required.";
            return false;
        }

        if (!TryValidateDetectionRule(app.PrimaryDetection, required: true, app.Name, "PrimaryDetection", out error))
        {
            return false;
        }

        if (app.SecondaryDetection is not null &&
            !TryValidateDetectionRule(app.SecondaryDetection, required: false, app.Name, "SecondaryDetection", out error))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(app.IconPath))
        {
            var iconPath = app.IconPath.Trim();
            if (iconPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = $"{app.Name}: IconPath contains invalid characters.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateDetectionRule(
        DetectionRule? rule,
        bool required,
        string appName,
        string ruleName,
        out string error)
    {
        if (rule is null)
        {
            error = required ? $"{appName}: {ruleName} is required." : string.Empty;
            return !required;
        }

        if (!Enum.IsDefined(rule.Type))
        {
            error = $"{appName}: {ruleName}.Type is invalid.";
            return false;
        }

        if (required && string.IsNullOrWhiteSpace(rule.Value))
        {
            error = $"{appName}: {ruleName}.Value is required.";
            return false;
        }

        if (!required && string.IsNullOrWhiteSpace(rule.Value))
        {
            error = string.Empty;
            return true;
        }

        error = string.Empty;
        return true;
    }

    public static List<string> ValidateConfig(AppCatalogueConfig config)
    {
        var errors = new List<string>();

        if (config is null)
        {
            errors.Add("Config is null.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(config.ConfigVersion))
        {
            errors.Add("ConfigVersion is required.");
        }

        if (config.Apps.Count == 0)
        {
            errors.Add("At least one app entry is required.");
            return errors;
        }

        var duplicateNames = config.Apps
            .Where(app => !string.IsNullOrWhiteSpace(app.Name))
            .GroupBy(app => app.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        foreach (var duplicate in duplicateNames)
        {
            errors.Add($"Duplicate app name found: {duplicate}");
        }

        foreach (var app in config.Apps)
        {
            if (!TryValidate(app, out var validationError))
            {
                errors.Add(validationError);
            }
        }

        return errors;
    }
}
