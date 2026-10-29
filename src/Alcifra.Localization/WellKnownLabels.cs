namespace Alcifra.Localization;

/// <summary>
/// The display text for a well-known mask's name or field name, in the reader's language (ADR 0891).
/// </summary>
/// <remarks>
/// <para>
/// A mask's name and its fields' names are STORED in English — they are identities: the core finds a field by its
/// name (the Manual's "Article number" is how Alcifra's own manual is found again), a module reads a value back
/// by it, and a search filter names it. Translating the stored name would break all three. So the translation
/// happens where a person reads it, here, once for both clients.
/// </para>
/// <para>
/// Only the names listed below are translated; any other name — a tenant's own mask, a module's — is shown exactly as
/// it was stored. Started with the two masks the manuals feature introduced; a well-known mask that wants its labels
/// translated adds its names here and its keys to all four resource files (<c>LocalizationKeyTests</c> enforces the
/// latter).
/// </para>
/// </remarks>
public static class WellKnownLabels
{
    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Manual"] = "WkMaskManual",
        ["Brand"] = "WkFieldBrand",
        ["Product name"] = "WkFieldProductName",
        ["Article number"] = "WkFieldArticleNumber",
        ["Product version"] = "WkFieldProductVersion",
        ["Serial numbers"] = "WkFieldSerialNumbers",
        ["Date of purchase"] = "WkFieldDateOfPurchase",
        ["Warranty expiry"] = "WkFieldWarrantyExpiry",
        ["Expected duration of use (years)"] = "WkFieldExpectedDurationOfUse",
        // The password store (ADR 0964).
        ["Password Store"] = "WkMaskPasswordStore",
        ["Password Folder"] = "WkMaskPasswordFolder",
        ["Password Entry"] = "WkMaskPasswordEntry",
        ["Exposed since"] = "WkFieldExposedSince",
    };

    /// <summary>The text to show for a stored mask or field name — translated where it is a known one.</summary>
    public static string For(string? storedName) => storedName switch
    {
        null => string.Empty,
        // The documentation mask is named after the product (ADR 0986): the present name, or an earlier default not yet healed.
        _ when storedName == CoreOwnedNames.DocumentationMask
            || CoreOwnedNames.IsEarlierDefault(storedName, CoreOwnedNames.DocumentationSuffix) => Strings.Get("WkMaskDocumentation"),
        _ when Keys.TryGetValue(storedName, out var key) => Strings.Get(key),
        _ => storedName,
    };
}
