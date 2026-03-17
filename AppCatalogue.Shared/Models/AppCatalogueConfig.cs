namespace AppCatalogue.Shared.Models;

public sealed class AppCatalogueConfig
{
    public string ConfigVersion { get; set; } = "1.0.0";
    public List<AppEntry> Apps { get; set; } = [];
}
