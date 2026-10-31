namespace Alcifra.Presentation;

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

    /// <summary>The DNS label a mail-domain claim publishes its TXT challenge under (#667).</summary>
    public const string MailDomainChallengeLabel = $"_{Product}-challenge";

    /// <summary>The prefix of a mail-domain challenge value, so a zone's owner can tell what the record is for.</summary>
    public const string MailDomainChallengePrefix = $"{Product}-domain-verification=";

    /// <summary>
    /// The environment variables the command-line tools share (<c>alcifra</c>, <c>apass</c>): the installation's address,
    /// the session's access token (both printed by <c>login</c> as shell exports), and a client secret, which is never an
    /// option. Wire in ADR 0987's sense: a script written against them must work whatever the build is called.
    /// </summary>
    public const string UrlVariable = "ALCIFRA_URL";

    /// <inheritdoc cref="UrlVariable"/>
    public const string TokenVariable = "ALCIFRA_TOKEN";

    /// <inheritdoc cref="UrlVariable"/>
    public const string ClientSecretVariable = "ALCIFRA_CLIENT_SECRET";

    /// <summary>The file extension of a user's sealed Messenger backup.</summary>
    public const string MessengerBackupExtension = $".{Product}-messages";

    /// <summary>The URL scheme the native clients register for deep links, <c>alcifra://go/{id}</c> (#761).</summary>
    public const string DeepLinkScheme = Product;

    /// <summary>
    /// The names before the rename (#1117, phase 4), still ACCEPTED so a client or an integration written against them
    /// keeps working while it moves: the server seeds the old client ids beside the new ones, accepts the old redirect,
    /// and a webhook carries both signature headers.
    /// </summary>
    /// <remarks>
    /// Retirement trigger (ADR 0885): remove this class and every use of it in the first core release after the one that
    /// introduced the alcifra names, i.e. v0.57.0 (release checklist, <c>docs/deploy/release.md</c>). The mobile redirect
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

        /// <summary>Where a domain claimed before the rename may still publish its challenge (phase 4b).</summary>
        public const string MailDomainChallengeLabel = "_simplarchive-challenge";

        /// <summary>The tools' environment variables before the rename, still read after the current ones (#1117).</summary>
        public const string CliUrlVariable = "SACONSOLE_URL";

        /// <inheritdoc cref="CliUrlVariable"/>
        public const string CliTokenVariable = "SACONSOLE_TOKEN";

        /// <inheritdoc cref="CliUrlVariable"/>
        public const string CliClientSecretVariable = "SACONSOLE_CLIENT_SECRET";

        /// <summary>The product-level session names <c>apass</c> printed before the rename (owner, 2026-10-25).</summary>
        public const string SessionUrlVariable = "SA_URL";

        /// <inheritdoc cref="SessionUrlVariable"/>
        public const string SessionTokenVariable = "SA_TOKEN";

        /// <summary>The deep-link scheme before the rename, still registered and parsed (#1117).</summary>
        public const string DeepLinkScheme = "simplarchive";
    }
}
