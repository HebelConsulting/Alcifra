namespace Alcifra.Application.Branding;

/// <summary>
/// A RUNTIME brand pack (ADR 1103, #2408): what a signed-in tenant wears — the product name, the palettes and the
/// asset files — loaded from the installation's configured pack directory rather than compiled in.
/// </summary>
/// <remarks>
/// The installation's own compiled brand is NOT a pack: it is what anonymous pages and a tenant with no pack wear.
/// A pack is only ever applied after authentication, which is what keeps the legal rule (no tenant brand on an
/// anonymous connection) structural instead of a rendering detail.
/// </remarks>
public sealed record BrandPack(
    string Id,
    string ProductName,
    BrandPalette Colors,
    IReadOnlyList<BrandPalette> ColorVariants,
    IReadOnlyDictionary<string, string> Assets,
    string Version)
{
    /// <summary>The palette with the given variant id, or null when the pack does not carry it.</summary>
    public BrandPalette? FindPalette(string? id) =>
        id is null ? null : ColorVariants.FirstOrDefault(palette => string.Equals(palette.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One palette of a pack: a variant (or the pack's own <see cref="BrandPack.Colors"/>) with its display names per
/// language and its tokens, split into the light and dark themes the clients apply.
/// </summary>
public sealed record BrandPalette(
    string? Id,
    IReadOnlyDictionary<string, string> Names,
    BrandTheme Light,
    BrandTheme Dark);

/// <summary>
/// A theme's tokens grouped exactly as the brand file groups them (<c>accent</c>, <c>semantic</c>, <c>neutral</c>),
/// so a pack's palette and an edition's compiled brand speak the same vocabulary to the clients' theming.
/// </summary>
public sealed record BrandTheme(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Groups);
