namespace AppCatalogue.Shared.Models;

public sealed class DetectionRuleSuggestion
{
    public DetectionType DetectionType { get; init; }
    public string DetectionValue { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Low";
    public string Reason { get; init; } = string.Empty;

    public string DisplayText =>
        $"{DetectionType} | {DetectionValue} | Confidence: {Confidence} | {Reason}";

    public override string ToString() => DisplayText;
}

public sealed class DetectionSuggestionResult
{
    public string Summary { get; init; } = string.Empty;
    public List<DetectionRuleSuggestion> Suggestions { get; init; } = [];
    public DetectionRuleSuggestion? RecommendedPrimaryDetection { get; init; }
    public DetectionRuleSuggestion? RecommendedSecondaryDetection { get; init; }
}
