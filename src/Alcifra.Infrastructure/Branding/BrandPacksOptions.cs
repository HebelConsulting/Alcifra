namespace Alcifra.Infrastructure.Branding;

/// <summary>
/// Where the installation's runtime brand packs live (ADR 1103, #2408): one subdirectory per pack, each with a
/// <c>pack.json</c> and its asset files. Null or empty means the installation carries no packs at all — every
/// ordinary customer installation — so the feature can only exist where packs were deliberately mounted.
/// </summary>
public sealed class BrandPacksOptions
{
    public const string SectionName = "Brand";
    public const string DirectoryKey = $"{SectionName}:PacksDirectory";

    /// <summary>The configured directory, or null when the installation has none.</summary>
    public string? Directory { get; set; }
}
