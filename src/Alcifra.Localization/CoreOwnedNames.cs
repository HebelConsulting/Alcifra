namespace Alcifra.Localization;

/// <summary>
/// The names the core gives what it creates in every tenant (ADRs 0986, 1095): the documentation mask, the core's
/// container folder, the manuals folder and the installation folder inside it, and the core manual. The container is
/// named after the installation's product (<see cref="Strings.ProductName"/>); the two folders in it carry fixed names.
/// A startup heal renames one that still carries an EARLIER default, never one a person renamed.
/// </summary>
public static class CoreOwnedNames
{
    public const string DocumentationSuffix = " Documentation";
    public const string ManualsSuffix = " Manuals";
    public const string ManualSuffix = " Manual";

    /// <summary>The manuals folder's fixed name inside the container (ADR 1095) — it carries no product reference.</summary>
    public const string ManualsFolderName = "Manuals";

    /// <summary>The installation's own artefacts folder inside the container (ADR 1095).</summary>
    public const string InstallationFolderName = "Installation";

    /// <summary>The documentation mask, which the manuals folder wears.</summary>
    public static string DocumentationMask => Strings.ProductName + DocumentationSuffix;

    /// <summary>The core's container folder — the product's name, healed like the other core-owned names.</summary>
    public static string CoreFolder => Strings.ProductName;

    /// <summary>The folder the core files the manuals into, inside the container (ADR 1095).</summary>
    public static string ManualsFolder => ManualsFolderName;

    /// <summary>The folder the installation files its own artefacts into, beside the manuals (ADR 1095).</summary>
    public static string InstallationFolder => InstallationFolderName;

    /// <summary>The core's own user manual.</summary>
    public static string CoreManual => Strings.ProductName + ManualSuffix;

    // The product names an earlier default may carry: the name before the rename (ADR 0985), and the build's own, for an
    // installation that has since named itself (Branding:ProductName).
    private static readonly string[] EarlierProducts = [PersistedNames.EarlierProduct, ProductBuild.Name];

    /// <summary>
    /// Whether <paramref name="current"/> is an earlier default of a core-owned name ending in <paramref name="suffix"/>,
    /// so a heal renames it to the present one; false for the present name and for anything a person chose. An empty
    /// suffix is the container's case (ADR 1095): the name is the product's own.
    /// </summary>
    public static bool IsEarlierDefault(string current, string suffix) =>
        !string.Equals(current, Strings.ProductName + suffix, StringComparison.Ordinal)
        && EarlierProducts.Any(product => string.Equals(current, product + suffix, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="current"/> is an EARLIER default of the manuals folder (ADR 1095): it was
    /// "&lt;product&gt; Manuals" until the core's own tree arrived, and the heal renames it to
    /// <see cref="ManualsFolder"/> — only when the core gave it that name, never after a person's rename.
    /// </summary>
    public static bool IsEarlierManualsFolderName(string current) =>
        !string.Equals(current, ManualsFolder, StringComparison.Ordinal)
        && EarlierProducts.Append(Strings.ProductName)
            .Any(product => string.Equals(current, product + ManualsSuffix, StringComparison.Ordinal));
}
