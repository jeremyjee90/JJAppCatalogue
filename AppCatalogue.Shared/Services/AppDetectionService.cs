using System.IO;
using AppCatalogue.Shared.Models;
using Microsoft.Win32;

namespace AppCatalogue.Shared.Services;

public sealed class AppDetectionService
{
    private static readonly RegistryHive[] Hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool IsInstalled(AppEntry app)
    {
        return EvaluateApp(app).IsInstalled;
    }

    public DetectionEvaluationResult EvaluateApp(AppEntry app)
    {
        var evaluations = new List<DetectionRuleEvaluation>();

        if (app.PrimaryDetection is null)
        {
            evaluations.Add(new DetectionRuleEvaluation
            {
                RuleName = "PrimaryDetection",
                DetectionType = DetectionType.RegistryDisplayName,
                DetectionValue = string.Empty,
                Passed = false,
                Detail = "PrimaryDetection is missing."
            });

            return new DetectionEvaluationResult
            {
                IsInstalled = false,
                Summary = "Primary detection rule is missing.",
                RuleResults = evaluations
            };
        }

        evaluations.Add(EvaluateRule("PrimaryDetection", app.PrimaryDetection));

        if (app.SecondaryDetection is not null && !string.IsNullOrWhiteSpace(app.SecondaryDetection.Value))
        {
            evaluations.Add(EvaluateRule("SecondaryDetection", app.SecondaryDetection));
        }

        var isInstalled = evaluations.Any(rule => rule.Passed);
        var summary = isInstalled
            ? "Detection matched at least one rule."
            : "Detection rule did not match on this machine. This does not necessarily mean the rule is incorrect.";

        return new DetectionEvaluationResult
        {
            IsInstalled = isInstalled,
            Summary = summary,
            RuleResults = evaluations
        };
    }

    public string ExplainRule(DetectionRule? rule)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.Value))
        {
            return "No detection rule configured.";
        }

        return rule.Type switch
        {
            DetectionType.RegistryDisplayName =>
                $"DetectionType: RegistryDisplayName{Environment.NewLine}" +
                $"Value: {rule.Value}{Environment.NewLine}{Environment.NewLine}" +
                "This rule searches uninstall registry entries under:" + Environment.NewLine +
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" + Environment.NewLine +
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" + Environment.NewLine +
                @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" + Environment.NewLine +
                $"and checks if DisplayName contains \"{rule.Value}\".",

            DetectionType.FileExists =>
                $"DetectionType: FileExists{Environment.NewLine}" +
                $"Value: {rule.Value}{Environment.NewLine}{Environment.NewLine}" +
                "This rule expands environment variables and checks if the file or directory exists.",

            DetectionType.RegistryKeyExists =>
                $"DetectionType: RegistryKeyExists{Environment.NewLine}" +
                $"Value: {rule.Value}{Environment.NewLine}{Environment.NewLine}" +
                "This rule checks for an existing registry key in HKLM and HKCU (32-bit and 64-bit views as applicable).",

            _ => "Unsupported detection type."
        };
    }

    public List<UninstallRegistryEntry> CaptureUninstallEntries()
    {
        var entries = new List<UninstallRegistryEntry>();
        foreach (var hive in Hives)
        {
            foreach (var view in Views)
            {
                using var baseKey = TryOpenBaseKey(hive, view);
                if (baseKey is null)
                {
                    continue;
                }

                using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subkeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subkeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    entries.Add(new UninstallRegistryEntry
                    {
                        DisplayName = displayName.Trim(),
                        RegistryPath = $"{HiveName(hive)}\\{UninstallKeyPath}\\{subkeyName}"
                    });
                }
            }
        }

        return entries
            .GroupBy(entry => $"{entry.RegistryPath}|{entry.DisplayName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private DetectionRuleEvaluation EvaluateRule(string ruleName, DetectionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Value))
        {
            return new DetectionRuleEvaluation
            {
                RuleName = ruleName,
                DetectionType = rule.Type,
                DetectionValue = string.Empty,
                Passed = false,
                Detail = $"{ruleName}.Value is empty."
            };
        }

        return rule.Type switch
        {
            DetectionType.RegistryDisplayName => EvaluateDisplayNameRule(ruleName, rule.Value),
            DetectionType.RegistryKeyExists => EvaluateRegistryKeyRule(ruleName, rule.Value),
            DetectionType.FileExists => EvaluateFileExistsRule(ruleName, rule.Value),
            _ => new DetectionRuleEvaluation
            {
                RuleName = ruleName,
                DetectionType = rule.Type,
                DetectionValue = rule.Value,
                Passed = false,
                Detail = "Unsupported detection type."
            }
        };
    }

    private DetectionRuleEvaluation EvaluateDisplayNameRule(string ruleName, string displayNamePattern)
    {
        var entries = CaptureUninstallEntries();
        var match = entries.FirstOrDefault(entry =>
            entry.DisplayName.Contains(displayNamePattern, StringComparison.OrdinalIgnoreCase));

        return new DetectionRuleEvaluation
        {
            RuleName = ruleName,
            DetectionType = DetectionType.RegistryDisplayName,
            DetectionValue = displayNamePattern,
            Passed = match is not null,
            MatchLocation = match?.RegistryPath ?? string.Empty,
            Detail = match is not null
                ? $"Matched uninstall DisplayName '{match.DisplayName}'."
                : "No uninstall DisplayName matched the configured pattern.",
            CheckedLocations =
            [
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            ]
        };
    }

    private DetectionRuleEvaluation EvaluateRegistryKeyRule(string ruleName, string registryPath)
    {
        var attemptedLocations = new List<string>();
        var normalizedPath = registryPath.Trim();

        var passed = false;
        var matchLocation = string.Empty;

        if (TryParseRegistryPath(normalizedPath, out var explicitHive, out var subKeyPath) && explicitHive.HasValue)
        {
            foreach (var view in Views)
            {
                attemptedLocations.Add($"{HiveName(explicitHive.Value)}\\{subKeyPath} ({view})");
                if (RegistryKeyExists(explicitHive.Value, subKeyPath, view))
                {
                    passed = true;
                    matchLocation = $"{HiveName(explicitHive.Value)}\\{subKeyPath} ({view})";
                    break;
                }
            }
        }
        else
        {
            var candidatePath = normalizedPath.TrimStart('\\');
            foreach (var hive in Hives)
            {
                foreach (var view in Views)
                {
                    attemptedLocations.Add($"{HiveName(hive)}\\{candidatePath} ({view})");
                    if (RegistryKeyExists(hive, candidatePath, view))
                    {
                        passed = true;
                        matchLocation = $"{HiveName(hive)}\\{candidatePath} ({view})";
                        break;
                    }
                }

                if (passed)
                {
                    break;
                }
            }
        }

        return new DetectionRuleEvaluation
        {
            RuleName = ruleName,
            DetectionType = DetectionType.RegistryKeyExists,
            DetectionValue = registryPath,
            Passed = passed,
            MatchLocation = matchLocation,
            Detail = passed ? "Registry key exists." : "Registry key was not found in tested hives/views.",
            CheckedLocations = attemptedLocations
        };
    }

    private static DetectionRuleEvaluation EvaluateFileExistsRule(string ruleName, string filePath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(filePath.Trim());
        var fileExists = File.Exists(expanded);
        var directoryExists = !fileExists && Directory.Exists(expanded);

        return new DetectionRuleEvaluation
        {
            RuleName = ruleName,
            DetectionType = DetectionType.FileExists,
            DetectionValue = filePath,
            Passed = fileExists || directoryExists,
            MatchLocation = fileExists || directoryExists ? expanded : string.Empty,
            Detail = fileExists
                ? "File exists."
                : directoryExists
                    ? "Directory exists."
                    : "File/directory path was not found.",
            CheckedLocations = [expanded]
        };
    }

    private static RegistryKey? TryOpenBaseKey(RegistryHive hive, RegistryView view)
    {
        try
        {
            return RegistryKey.OpenBaseKey(hive, view);
        }
        catch
        {
            return null;
        }
    }

    private static bool RegistryKeyExists(RegistryHive hive, string subKeyPath, RegistryView view)
    {
        using var baseKey = TryOpenBaseKey(hive, view);
        if (baseKey is null)
        {
            return false;
        }

        using var key = baseKey.OpenSubKey(subKeyPath);
        return key is not null;
    }

    private static bool TryParseRegistryPath(string path, out RegistryHive? hive, out string subKeyPath)
    {
        var normalized = path.Trim().Replace('/', '\\');

        if (normalized.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.LocalMachine;
            return TryExtractSubKey(normalized, out subKeyPath);
        }

        if (normalized.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.CurrentUser;
            return TryExtractSubKey(normalized, out subKeyPath);
        }

        hive = null;
        subKeyPath = normalized;
        return false;
    }

    private static bool TryExtractSubKey(string path, out string subKeyPath)
    {
        var separator = path.IndexOf('\\');
        if (separator < 0 || separator == path.Length - 1)
        {
            subKeyPath = string.Empty;
            return false;
        }

        subKeyPath = path[(separator + 1)..];
        return true;
    }

    private static string HiveName(RegistryHive hive)
    {
        return hive switch
        {
            RegistryHive.LocalMachine => "HKLM",
            RegistryHive.CurrentUser => "HKCU",
            _ => hive.ToString()
        };
    }
}
