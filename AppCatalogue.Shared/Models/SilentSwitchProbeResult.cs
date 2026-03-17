namespace AppCatalogue.Shared.Models;

public sealed class SilentSwitchProbeResult
{
    public bool IsMsi { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Low";
    public string CommandPreview { get; init; } = string.Empty;
    public string RawOutput { get; init; } = string.Empty;
    public List<string> Suggestions { get; init; } = [];
    public List<SilentSwitchProbeAttempt> Attempts { get; init; } = [];
}

public sealed class SilentSwitchProbeAttempt
{
    public string Arguments { get; init; } = string.Empty;
    public bool Started { get; init; }
    public bool TimedOut { get; init; }
    public int? ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
}
