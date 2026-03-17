namespace AppCatalogue.Shared.Models;

public sealed class AppConfigLoadResult
{
    public required AppCatalogueConfig Config { get; init; }
    public required List<string> Errors { get; init; }
}
