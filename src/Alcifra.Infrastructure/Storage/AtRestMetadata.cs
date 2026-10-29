namespace Alcifra.Infrastructure.Storage;

/// <summary>
/// The object-metadata keys of an object encrypted at rest (ADRs 0818, 0867): its wrapped DEK, the KEK generation that
/// wrapped it and that key's thumbprint. Written as <c>alcifra-*</c>; read as either <c>alcifra-*</c> or the
/// <c>sa-*</c> keys before the rename (ADR 1003).
/// </summary>
/// <remarks>
/// <b>The old keys are read FOR EVER, unlike every other name before the rename</b> (ADR 1001): a WORM-locked object
/// can never be rewritten, so its <c>sa-*</c> metadata outlives any release. A re-wrap (the KEK sweep, the strict tier's
/// finalize) writes the new keys and removes the old, so every object that can change moves on its own.
/// </remarks>
public static class AtRestMetadata
{
    public const string WrappedDekKey = "alcifra-wrapped-dek";

    public const string KekGenerationKey = "alcifra-kek-generation";

    /// <summary>Which KEY the generation was when it wrapped this object (ADR 0867); absent means unknown, never mismatched.</summary>
    public const string KekThumbprintKey = "alcifra-kek-thumbprint";

    public const string LegacyWrappedDekKey = "sa-wrapped-dek";

    public const string LegacyKekGenerationKey = "sa-kek-generation";

    public const string LegacyKekThumbprintKey = "sa-kek-thumbprint";

    /// <summary>Whether the object is encrypted at rest: it carries a wrapped DEK under either name.</summary>
    public static bool IsEncrypted(IReadOnlyDictionary<string, string> metadata) => WrappedDek(metadata) is not null;

    public static string? WrappedDek(IReadOnlyDictionary<string, string> metadata) =>
        Either(metadata, WrappedDekKey, LegacyWrappedDekKey);

    public static string? Generation(IReadOnlyDictionary<string, string> metadata) =>
        Either(metadata, KekGenerationKey, LegacyKekGenerationKey);

    public static string? Thumbprint(IReadOnlyDictionary<string, string> metadata) =>
        Either(metadata, KekThumbprintKey, LegacyKekThumbprintKey);

    /// <summary>
    /// Writes the wrapping into <paramref name="metadata"/> under the current keys and removes the keys before the rename,
    /// so a re-wrapped object never carries two answers. A null thumbprint leaves none (unknown).
    /// </summary>
    public static void Stamp(IDictionary<string, string> metadata, string wrappedDek, string generation, string? thumbprint)
    {
        metadata.Remove(LegacyWrappedDekKey);
        metadata.Remove(LegacyKekGenerationKey);
        metadata.Remove(LegacyKekThumbprintKey);
        metadata[WrappedDekKey] = wrappedDek;
        metadata[KekGenerationKey] = generation;
        if (thumbprint is { Length: > 0 })
        {
            metadata[KekThumbprintKey] = thumbprint;
        }
        else
        {
            metadata.Remove(KekThumbprintKey);
        }
    }

    // The current key wins: an object carries one set, and a re-wrap removes the other.
    private static string? Either(IReadOnlyDictionary<string, string> metadata, string key, string legacyKey) =>
        metadata.TryGetValue(key, out var value) || metadata.TryGetValue(legacyKey, out value) ? value : null;
}
