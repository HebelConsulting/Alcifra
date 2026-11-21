namespace Alcifra.Theming;

/// <summary>
/// The asset names a client looks for in a runtime brand pack (ADR 1103, #2408). A pack is free to declare other
/// files for its own use; these three are the WELL-KNOWN names a client may ask for, and the pack's manifest is the
/// whitelist the API serves from.
/// </summary>
/// <remarks>
/// They live here, in the dependency-free leaf both the web client and the API read, rather than in either of them:
/// a name a client looks for and a name a pack declares have to be the same string, and two copies of a convention
/// are two conventions waiting to drift.
/// </remarks>
public static class BrandPackAssets
{
    /// <summary>The wordmark for light backgrounds (shown while the page is in its light theme).</summary>
    public const string WordmarkLight = "wordmarkLight";

    /// <summary>The wordmark for dark backgrounds.</summary>
    public const string WordmarkDark = "wordmarkDark";

    /// <summary>The small icon a browser shows in its tab.</summary>
    public const string Favicon = "favicon";
}
