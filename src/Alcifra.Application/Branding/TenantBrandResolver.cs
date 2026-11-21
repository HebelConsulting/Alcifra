namespace Alcifra.Application.Branding;

/// <summary>
/// What a caller wears after authentication (ADR 1103, #2408): the tenant's pack when the installation carries it,
/// otherwise the installation's own brand; and the palette chosen by the user (only while the tenant allows the
/// override), then the tenant's default, then the pack's default.
/// </summary>
public sealed record EffectiveBrand(
    string? PackId,
    string ProductName,
    string? PaletteId,
    BrandPalette? Palette,
    IReadOnlyList<BrandPalette> Palettes,
    bool CanChoosePalette,
    IReadOnlyDictionary<string, string> Assets,
    string? Version)
{
    /// <summary>The installation's own brand — no pack, no palette, nothing to choose.</summary>
    public static EffectiveBrand Installation(string productName) =>
        new(null, productName, null, null, [], false, new Dictionary<string, string>(), null);
}

/// <summary>
/// Resolves a caller's effective brand. Pure arithmetic over the tenant's and user's stored choices and the loaded
/// packs — the one place the fallback order lives, so both clients and the API answer identically.
/// </summary>
public sealed class TenantBrandResolver(IBrandPackStore packs, string installationProductName)
{
    /// <summary>
    /// A tenant pack the installation does not carry falls back to the installation brand (the load already logged
    /// the missing pack at Warning). A palette that is not one of the current pack's variants falls back silently:
    /// a tenant may switch brand and leave a stored palette behind, which must not break the page it paints.
    /// </summary>
    public EffectiveBrand Resolve(string? tenantPackId, string? tenantPalette, bool allowUserOverride, string? userPalette)
    {
        var pack = packs.Find(tenantPackId);
        if (pack is null)
        {
            return EffectiveBrand.Installation(installationProductName);
        }

        var chosen = (allowUserOverride ? pack.FindPalette(userPalette) : null)
                     ?? pack.FindPalette(tenantPalette)
                     ?? pack.Colors;

        return new EffectiveBrand(
            pack.Id,
            pack.ProductName,
            chosen.Id,
            chosen,
            pack.ColorVariants,
            CanChoosePalette: allowUserOverride && pack.ColorVariants.Count > 1,
            pack.Assets,
            pack.Version);
    }
}
