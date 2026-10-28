using System.Globalization;
using System.Resources;

namespace SimplArchive.Localization;

// The localized-strings accessor (ADR "Web UI localization — shared resources"; originally ADR 0468). Reads
// from the embedded Strings.resx (English / invariant) and the de/it/es satellites, keyed by the current UI
// culture. A missing key falls back to the key itself, so a not-yet-translated string is visible (as its key)
// rather than blank. Shared by the Avalonia desktop and the Blazor WASM web client.
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("SimplArchive.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The product's name as the build ships it (core #1117, Mobile #187): the ProductName build property.</summary>
    public const string DefaultProductName = ProductBuild.Name;

    /// <summary>The placeholder a text uses where it names the product.</summary>
    public const string ProductToken = "{Product}";

    /// <summary>
    /// The product's name in every text that names it (written <see cref="ProductToken"/> in the resources): ONE value, so
    /// the name, and later its casing, changes in one place. A host sets it once at startup (the phone from its build);
    /// unset, it is <see cref="DefaultProductName"/>. Stored names (the manuals folder, the documentation mask) and
    /// identifiers (a header name) are not texts and keep their spelling.
    /// </summary>
    public static string ProductName { get; set; } = DefaultProductName;

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    // An explicit-culture overload so a test can resolve a language without mutating global culture state.
    public static string Get(string key, CultureInfo culture) =>
        (Manager.GetString(key, culture) ?? key).Replace(ProductToken, ProductName, StringComparison.Ordinal);

    // Every resource key (from the invariant/English set) — lets a test assert full translation coverage.
    public static IEnumerable<string> AllKeys()
    {
        var set = Manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
        if (set is null)
        {
            yield break;
        }

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            yield return (string)entry.Key;
        }
    }
}
