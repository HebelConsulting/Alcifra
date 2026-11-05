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

    /// <summary>
    /// The hidden, read-only file at the WebDAV root naming whose mount it is: the account's address and its tenant's
    /// id, one per line (#2053). Every user and tenant of an installation share one mount address, so this is how a
    /// client tells its own mounted volume from another user's.
    /// </summary>
    public const string WebDavAccountFile = ".alcifra-account";

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

    /// <summary>The identifier a device configuration profile's payloads carry (#1970): a device keeps it across brands, so
    /// installing a device's profile again replaces the one before rather than adding a second.</summary>
    public const string DeviceProfileIdentifierPrefix = $"com.{Product}.device";

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
}
