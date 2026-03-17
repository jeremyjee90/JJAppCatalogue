using System.Text.Json.Serialization;

namespace AppCatalogue.Shared.Models;

public sealed class AppEntry
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public bool Enabled { get; set; } = true;
    public InstallerSourceType InstallerSourceType { get; set; } = InstallerSourceType.FileServer;
    public string InstallerPath { get; set; } = string.Empty;
    public string SilentArguments { get; set; } = string.Empty;
    public string WingetId { get; set; } = string.Empty;
    public string WingetArguments { get; set; } = "--silent --accept-package-agreements --accept-source-agreements";
    public DetectionRule PrimaryDetection { get; set; } = new()
    {
        Type = DetectionType.RegistryDisplayName,
        Value = string.Empty
    };
    public DetectionRule? SecondaryDetection { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("DetectionType")]
    public DetectionType? LegacyDetectionType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("DetectionValue")]
    public string? LegacyDetectionValue { get; set; }

    public string IconPath { get; set; } = string.Empty;
    public bool RequiresAdmin { get; set; } = false;
}
