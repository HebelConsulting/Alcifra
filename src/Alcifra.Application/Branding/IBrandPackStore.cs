namespace Alcifra.Application.Branding;

/// <summary>
/// The installation's runtime brand packs (ADR 1103) — read-only, loaded once from the configured directory.
/// </summary>
/// <remarks>
/// An installation with no pack directory has an EMPTY store, which is every ordinary customer installation: the
/// feature can only be exercised where packs were deliberately installed (the kiosk). A pack that fails to parse is
/// left out with a Warning at load — and the resolution then falls back to the installation's own brand, which is
/// the safe rendering, never a half-applied one.
/// </remarks>
public interface IBrandPackStore
{
    /// <summary>The ids of the packs that loaded, sorted.</summary>
    IReadOnlyList<string> AvailablePackIds { get; }

    /// <summary>The pack with this id, or null when the installation does not carry it.</summary>
    BrandPack? Find(string? packId);

    /// <summary>
    /// Opens one of the pack's DECLARED asset files — the name must be one the pack's manifest lists, so a caller
    /// can never address a path the pack did not publish (no traversal, no stray files).
    /// </summary>
    BrandAssetSource? Open(string packId, string assetName);
}

/// <summary>An asset file of a pack, with the content type its extension names.</summary>
/// <param name="Content">The bytes, owned by the caller.</param>
/// <param name="ContentType">Media type derived from the file extension.</param>
public sealed record BrandAssetSource(Stream Content, string ContentType);
