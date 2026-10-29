namespace SimplArchive.Localization;

/// <summary>
/// The names the core gives what it creates in every tenant (ADR 0986): the documentation mask, the manuals folder and
/// the core manual, each named after the installation's product (<see cref="Strings.ProductName"/>). A startup heal
/// renames one that still carries an EARLIER default, never one a person renamed.
/// </summary>
public static class CoreOwnedNames
{
    public const string DocumentationSuffix = " Documentation";
    public const string ManualsSuffix = " Manuals";
    public const string ManualSuffix = " Manual";

    /// <summary>The documentation mask, which the manuals folder wears.</summary>
    public static string DocumentationMask => Strings.ProductName + DocumentationSuffix;

    /// <summary>The folder the core files the manuals into.</summary>
    public static string ManualsFolder => Strings.ProductName + ManualsSuffix;

    /// <summary>The core's own user manual.</summary>
    public static string CoreManual => Strings.ProductName + ManualSuffix;

    // The product names an earlier default may carry: the name before the rename (ADR 0985), and the build's own, for an
    // installation that has since named itself (Branding:ProductName).
    private static readonly string[] EarlierProducts = [PersistedNames.EarlierProduct, ProductBuild.Name];

    /// <summary>
    /// Whether <paramref name="current"/> is an earlier default of a core-owned name ending in <paramref name="suffix"/>,
    /// so a heal renames it to the present one; false for the present name and for anything a person chose.
    /// </summary>
    public static bool IsEarlierDefault(string current, string suffix) =>
        !string.Equals(current, Strings.ProductName + suffix, StringComparison.Ordinal)
        && EarlierProducts.Any(product => string.Equals(current, product + suffix, StringComparison.Ordinal));
}
