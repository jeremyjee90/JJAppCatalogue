namespace AppCatalogue.Shared.Models;

public sealed class HyperVDiscoveryRequest
{
    public string AppName { get; set; } = string.Empty;
    public string InstallerPath { get; set; } = string.Empty;
    public string PreferredSilentArguments { get; set; } = string.Empty;
    public DiscoveryModeSettings Settings { get; set; } = new();
    public string GuestScriptSourceDirectory { get; set; } = string.Empty;
}

public sealed class HyperVDiscoveryJob
{
    public string JobId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string InstallerFileName { get; set; } = string.Empty;
    public string PreferredSilentArguments { get; set; } = string.Empty;
    public int ProbeTimeoutSeconds { get; set; } = 15;
    public int InstallerTimeoutSeconds { get; set; } = 1200;
    public bool ShutdownVmOnComplete { get; set; } = true;
    public DateTime SubmittedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class HyperVDiscoveryRunResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string HostResultDirectory { get; set; } = string.Empty;
    public string RawResultPath { get; set; } = string.Empty;
    public HyperVDiscoveryResult DiscoveryResult { get; set; } = new();
    public List<string> Errors { get; set; } = [];
}

public sealed class HyperVDiscoveryResult
{
    public bool Success { get; set; }
    public string InstallerType { get; set; } = string.Empty;
    public List<SilentSwitchSuggestion> SilentSwitchSuggestions { get; set; } = [];
    public DetectionRecommendation? PrimaryDetection { get; set; }
    public DetectionRecommendation? SecondaryDetection { get; set; }
    public DiscoveryEvidence Evidence { get; set; } = new();
    public List<string> Errors { get; set; } = [];
    public string RawHelpOutput { get; set; } = string.Empty;
    public string InstallAttemptSummary { get; set; } = string.Empty;
}

public sealed class SilentSwitchSuggestion
{
    public string Arguments { get; set; } = string.Empty;
    public string Confidence { get; set; } = "Low";
    public string Reason { get; set; } = string.Empty;

    public string DisplayText =>
        $"{Arguments} ({Confidence}) - {Reason}";

    public override string ToString() => DisplayText;
}

public sealed class DetectionRecommendation
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Confidence { get; set; } = "Low";
    public string Reason { get; set; } = string.Empty;

    public string DisplayText =>
        $"{Type}: {Value} ({Confidence}) - {Reason}";

    public override string ToString() => DisplayText;
}

public sealed class DiscoveryEvidence
{
    public List<string> NewUninstallEntries { get; set; } = [];
    public List<string> NewFiles { get; set; } = [];
    public List<string> NewRegistryKeys { get; set; } = [];
    public List<string> ProbeAttempts { get; set; } = [];
}

public enum DiscoveryProgressStage
{
    PreparingLab,
    RestoringCheckpoint,
    StartingVm,
    WaitingForGuest,
    CopyingInstaller,
    SubmittingDiscoveryJob,
    WaitingForGuestResults,
    CollectingResults,
    RevertingVm,
    Complete,
    Failed
}

public sealed class DiscoveryProgressUpdate
{
    public DiscoveryProgressStage Stage { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}
