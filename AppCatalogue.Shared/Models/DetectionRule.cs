namespace AppCatalogue.Shared.Models;

public sealed class DetectionRule
{
    public DetectionType Type { get; set; } = DetectionType.RegistryDisplayName;
    public string Value { get; set; } = string.Empty;
}
