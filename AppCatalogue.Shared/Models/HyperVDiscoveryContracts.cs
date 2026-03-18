using System.Text.Json.Serialization;

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
    public string HostJobDirectory { get; set; } = string.Empty;
    public string HostLogPath { get; set; } = string.Empty;
    public string HostStatusPath { get; set; } = string.Empty;
    public string GuestArtifactsDirectory { get; set; } = string.Empty;
    public string GuestLogPath { get; set; } = string.Empty;
    public string GuestStatusPath { get; set; } = string.Empty;
    public string GuestResultPath { get; set; } = string.Empty;
    public string RawResultPath { get; set; } = string.Empty;
    public string CurrentStage { get; set; } = string.Empty;
    public string FinalStage { get; set; } = string.Empty;
    public DiscoveryStatusContract? Status { get; set; }
    public HyperVDiscoveryResult DiscoveryResult { get; set; } = new();
    public List<string> Errors { get; set; } = [];
}

public sealed class HyperVDiscoveryResult
{
    public bool Success { get; set; }
    public string InstallerType { get; set; } = string.Empty;
    public List<SilentSwitchSuggestion> SilentSwitchSuggestions { get; set; } = [];
    public SilentSwitchRecommendation SilentRecommendation { get; set; } = new();
    public List<SilentSwitchAttemptRecord> SilentSwitchAttemptHistory { get; set; } = [];
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

public sealed class SilentSwitchRecommendation
{
    public string RecommendedCommand { get; set; } = string.Empty;
    public string RecommendedArguments { get; set; } = string.Empty;
    public string ConfidenceLabel { get; set; } = "Low";
    public double ConfidenceScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool ManualReviewNeeded { get; set; }
}

public sealed class SilentSwitchAttemptRecord
{
    public int AttemptNumber { get; set; }
    public string CandidateSource { get; set; } = string.Empty;
    public string SelectionReason { get; set; } = string.Empty;
    public string InstallerFingerprint { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool ProcessStarted { get; set; }
    public bool TimedOut { get; set; }
    public bool Completed { get; set; }
    public int? ExitCode { get; set; }
    public bool RebootRequired { get; set; }
    public bool InstallationArtifactsDetected { get; set; }
    public int NewUninstallEntriesCount { get; set; }
    public int NewFilesCount { get; set; }
    public int NewServicesCount { get; set; }
    public int NewShortcutsCount { get; set; }
    public int NewProcessesCount { get; set; }
    public bool Pass { get; set; }
    public string Assessment { get; set; } = string.Empty;
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
    public List<string> NewServices { get; set; } = [];
    public List<string> NewShortcuts { get; set; } = [];
    public List<string> NewProcesses { get; set; } = [];
    public List<string> ProbeAttempts { get; set; } = [];
}

public sealed class DiscoveryStatusContract
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("updatedUtc")]
    public string UpdatedUtc { get; set; } = string.Empty;

    [JsonPropertyName("resultPath")]
    public string ResultPath { get; set; } = string.Empty;

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = string.Empty;
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
