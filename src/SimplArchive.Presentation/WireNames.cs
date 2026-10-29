namespace SimplArchive.Presentation;

/// <summary>
/// The names the product speaks ON THE WIRE (ADR 0987): fixed, whatever the build or the installation is called. A
/// brand changes what a person reads, never what a client or a server sends, so a white-labelled server answers every
/// client the same. Nothing here may be derived from the product's name.
/// </summary>
public static class WireNames
{
    /// <summary>The product's name in protocol identifiers, e.g. an iCalendar PRODID.</summary>
    public const string Product = "alcifra";

    /// <summary>The WebDAV mount's single path segment; the OS labels a mounted volume with it.</summary>
    public const string WebDavSegment = "webdav";

    /// <summary>The desktop client's OAuth client id.</summary>
    public const string DesktopClientId = $"{Product}-desktop";

    /// <summary>The mobile app's OAuth client id.</summary>
    public const string MobileClientId = $"{Product}-mobile";

    /// <summary>The mobile app's autofill extension's OAuth client id (ADR 0981).</summary>
    public const string MobileAutofillClientId = $"{Product}-mobile-autofill";

    /// <summary>The administrative CLI's (<c>alcifra</c>) OAuth client id.</summary>
    public const string CliClientId = $"{Product}-cli";

    /// <summary>The password CLI's (<c>apass</c>) OAuth client id.</summary>
    public const string PassClientId = $"{Product}-pass";

    /// <summary>The mobile app's redirect URI, on the scheme of its identifier <c>com.alcifra.mobile</c>.</summary>
    public const string MobileRedirectUri = $"com.{Product}.mobile:/oauth2redirect";

    /// <summary>The prefix of every header the product writes: an IMAP message's details, a webhook's signature.</summary>
    public const string HeaderPrefix = "X-Alcifra-";

    /// <summary>The header carrying an audit webhook's HMAC signature.</summary>
    public const string SignatureHeader = $"{HeaderPrefix}Signature";

    /// <summary>The IMAP header naming a message's mask.</summary>
    public const string MaskHeader = $"{HeaderPrefix}Mask";

    /// <summary>
    /// The names before the rename (#1117, phase 4), still ACCEPTED so a client or an integration written against them
    /// keeps working while it moves: the server seeds the old client ids beside the new ones, accepts the old redirect,
    /// and a webhook carries both signature headers.
    /// </summary>
    /// <remarks>
    /// Retirement trigger (ADR 0885): remove this class and every use of it in the first core release after the one that
    /// introduced the alcifra names, i.e. v0.56.0 (release checklist, <c>docs/deploy/release.md</c>). The mobile redirect
    /// waits for the app's own identifier to move (Mobile #187), and goes with that release instead if it is later.
    /// </remarks>
    public static class Legacy
    {
        public const string DesktopClientId = "simplarchive-desktop";

        public const string MobileClientId = "simplarchive-mobile";

        public const string MobileAutofillClientId = "simplarchive-mobile-autofill";

        public const string CliClientId = "saconsole";

        public const string PassClientId = "sapass";

        public const string MobileRedirectUri = "dev.simplarchive.mobile:/oauth2redirect";

        public const string SignatureHeader = "X-SimplArchive-Signature";
    }
}
