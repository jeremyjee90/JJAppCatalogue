namespace AppCatalogue.Shared.Models;

public sealed class DetectionRuleEvaluation
{
    public required string RuleName { get; init; }
    public required DetectionType DetectionType { get; init; }
    public required string DetectionValue { get; init; }
    public required bool Passed { get; init; }
    public string MatchLocation { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public List<string> CheckedLocations { get; init; } = [];
}

public sealed class DetectionEvaluationResult
{
    public bool IsInstalled { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<DetectionRuleEvaluation> RuleResults { get; init; } = [];
}

public sealed class UninstallRegistryEntry
{
    public required string DisplayName { get; init; }
    public required string RegistryPath { get; init; }
}
