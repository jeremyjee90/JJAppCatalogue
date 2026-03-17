using System.Diagnostics;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class DetectionSuggestionService
{
    private readonly FileLogger _logger;

    public DetectionSuggestionService(FileLogger logger)
    {
        _logger = logger;
    }

    public DetectionSuggestionResult Suggest(
        AppEntry app,
        string installerPath,
        string silentSwitchOutput = "")
    {
        var appName = (app.Name ?? string.Empty).Trim();
        var normalizedInstallerPath = (installerPath ?? string.Empty).Trim();
        var installerFileName = Path.GetFileNameWithoutExtension(normalizedInstallerPath);
        var metadata = ReadInstallerMetadata(normalizedInstallerPath);

        var signalText = string.Join(
            " ",
            appName,
            installerFileName,
            metadata.ProductName,
            metadata.FileDescription,
            metadata.OriginalFileName,
            silentSwitchOutput).ToLowerInvariant();

        _logger.Log($"Detection suggestion started for '{appName}' with installer '{normalizedInstallerPath}'.");

        var suggestions = new List<DetectionRuleSuggestion>();
        var matchedHeuristics = KnownHeuristics
            .Where(heuristic => heuristic.Keywords.Any(keyword => signalText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var heuristic in matchedHeuristics)
        {
            suggestions.AddRange(heuristic.Suggestions);
        }

        var bestDisplayName = FirstNonEmpty(metadata.ProductName, appName, installerFileName);
        if (!string.IsNullOrWhiteSpace(bestDisplayName) &&
            suggestions.All(s => s.DetectionType != DetectionType.RegistryDisplayName))
        {
            suggestions.Add(CreateSuggestion(
                DetectionType.RegistryDisplayName,
                bestDisplayName,
                matchedHeuristics.Count > 0 ? "Medium" : "Low",
                "Derived from app/import metadata."));
        }

        if (suggestions.Count == 0 && !string.IsNullOrWhiteSpace(bestDisplayName))
        {
            var normalizedName = NormalizeForFileName(bestDisplayName);
            suggestions.Add(CreateSuggestion(
                DetectionType.FileExists,
                $@"%ProgramFiles%\{bestDisplayName}\{normalizedName}.exe",
                "Low",
                "Generic Program Files path guess."));
            suggestions.Add(CreateSuggestion(
                DetectionType.FileExists,
                $@"%ProgramFiles(x86)%\{bestDisplayName}\{normalizedName}.exe",
                "Low",
                "Generic Program Files (x86) path guess."));
        }

        var deduplicated = suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.DetectionValue))
            .GroupBy(
                s => $"{s.DetectionType}|{s.DetectionValue.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => ConfidenceScore(item.Confidence))
                .First())
            .OrderBy(s => DetectionTypePriority(s.DetectionType))
            .ThenByDescending(s => ConfidenceScore(s.Confidence))
            .ThenBy(s => s.DetectionValue, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = deduplicated.Count > 0
            ? $"Generated {deduplicated.Count} detection suggestion(s). Review before applying."
            : "No reliable detection rule could be suggested automatically. Please configure detection manually.";

        var recommendedPrimary = deduplicated
            .OrderBy(s => DetectionTypePriority(s.DetectionType))
            .ThenByDescending(s => ConfidenceScore(s.Confidence))
            .FirstOrDefault();
        var recommendedSecondary = deduplicated
            .Where(s => recommendedPrimary is null ||
                        !string.Equals(s.DetectionValue, recommendedPrimary.DetectionValue, StringComparison.OrdinalIgnoreCase) ||
                        s.DetectionType != recommendedPrimary.DetectionType)
            .Where(s => s.DetectionType != DetectionType.RegistryDisplayName ||
                        (recommendedPrimary is not null && recommendedPrimary.DetectionType != DetectionType.RegistryDisplayName))
            .OrderBy(s => s.DetectionType == DetectionType.FileExists ? 0 : 1)
            .ThenByDescending(s => ConfidenceScore(s.Confidence))
            .FirstOrDefault();

        _logger.Log(
            $"Detection suggestion completed for '{appName}'. Suggestions={deduplicated.Count}, MatchedHeuristics={matchedHeuristics.Count}.");

        return new DetectionSuggestionResult
        {
            Summary = summary,
            Suggestions = deduplicated,
            RecommendedPrimaryDetection = recommendedPrimary,
            RecommendedSecondaryDetection = recommendedSecondary
        };
    }

    private static InstallerMetadata ReadInstallerMetadata(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return InstallerMetadata.Empty;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(installerPath);
            return new InstallerMetadata(
                versionInfo.ProductName ?? string.Empty,
                versionInfo.FileDescription ?? string.Empty,
                versionInfo.OriginalFilename ?? string.Empty);
        }
        catch
        {
            return InstallerMetadata.Empty;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "app";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(value.Where(ch => !invalidChars.Contains(ch)).ToArray());
        safe = safe.Replace(" ", string.Empty);
        return string.IsNullOrWhiteSpace(safe) ? "app" : safe;
    }

    private static DetectionRuleSuggestion CreateSuggestion(
        DetectionType detectionType,
        string detectionValue,
        string confidence,
        string reason)
    {
        return new DetectionRuleSuggestion
        {
            DetectionType = detectionType,
            DetectionValue = detectionValue,
            Confidence = confidence,
            Reason = reason
        };
    }

    private static int DetectionTypePriority(DetectionType detectionType)
    {
        return detectionType switch
        {
            DetectionType.RegistryDisplayName => 1,
            DetectionType.FileExists => 2,
            DetectionType.RegistryKeyExists => 3,
            _ => 4
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

    private static readonly List<KnownAppHeuristic> KnownHeuristics =
    [
        new KnownAppHeuristic(
            "Google Chrome",
            ["google chrome", "chrome"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Google Chrome", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "Medium", "Known app paths registry key.")
            ]),
        new KnownAppHeuristic(
            "Mozilla Firefox",
            ["mozilla firefox", "firefox"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Mozilla Firefox", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\Mozilla Firefox\firefox.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\Mozilla\Mozilla Firefox", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "Discord",
            ["discord"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Discord", "Medium", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%LocalAppData%\Discord\Update.exe", "High", "Known per-user install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKCU\Software\Discord", "Medium", "Known user registry key.")
            ]),
        new KnownAppHeuristic(
            "7-Zip",
            ["7-zip", "7zip"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "7-Zip", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\7-Zip\7zFM.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\7-Zip\7zFM.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\7-Zip", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "Notepad++",
            ["notepad++", "notepadpp"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Notepad++", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\Notepad++\notepad++.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\Notepad++\notepad++.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\Notepad++", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "VLC",
            ["vlc", "videolan"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "VLC media player", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\VideoLAN\VLC\vlc.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\VideoLAN\VLC", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "Adobe Acrobat Reader",
            ["acrobat reader", "adobe reader", "acroread", "acrobat"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Adobe Acrobat Reader", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\Adobe\Acrobat Reader", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "Zoom",
            ["zoom"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "Zoom", "Medium", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\Zoom\bin\Zoom.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\ZoomUMX", "Low", "Likely registry key.")
            ]),
        new KnownAppHeuristic(
            "TeamViewer",
            ["teamviewer"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "TeamViewer", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\TeamViewer\TeamViewer.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\TeamViewer\TeamViewer.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\TeamViewer", "Medium", "Known registry key.")
            ]),
        new KnownAppHeuristic(
            "AnyDesk",
            ["anydesk"],
            [
                CreateSuggestion(DetectionType.RegistryDisplayName, "AnyDesk", "High", "Known app display name."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles%\AnyDesk\AnyDesk.exe", "High", "Known install path."),
                CreateSuggestion(DetectionType.FileExists, @"%ProgramFiles(x86)%\AnyDesk\AnyDesk.exe", "Medium", "Known x86 install path."),
                CreateSuggestion(DetectionType.RegistryKeyExists, @"HKLM\SOFTWARE\AnyDesk", "Medium", "Known registry key.")
            ])
    ];

    private sealed record InstallerMetadata(string ProductName, string FileDescription, string OriginalFileName)
    {
        public static InstallerMetadata Empty { get; } = new(string.Empty, string.Empty, string.Empty);
    }

    private sealed record KnownAppHeuristic(
        string AppName,
        string[] Keywords,
        List<DetectionRuleSuggestion> Suggestions);
}
