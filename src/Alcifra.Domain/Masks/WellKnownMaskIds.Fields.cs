namespace Alcifra.Domain.Masks;

// The well-known masks' field names, split from the ids and rules beside them (the 1000-line rule): the seeder and every
// reader of a field spell it from here, so no two of them can name one differently.
public static partial class WellKnownMaskIds
{
    /// <summary>The Manual mask's field names (ADR 0891), in display order — the seeder's list and the core's filer
    /// both read these, so the two cannot spell a field differently.</summary>
    public static class ManualFields
    {
        public const string Brand = "Brand";
        public const string ProductName = "Product name";
        public const string ArticleNumber = "Article number";
        public const string ProductVersion = "Product version";
        public const string SerialNumbers = "Serial numbers";
        public const string DateOfPurchase = "Date of purchase";
        public const string WarrantyExpiry = "Warranty expiry";
        public const string ExpectedDurationOfUse = "Expected duration of use (years)";
    }

    /// <summary>The Sealed Document mask's one field (owner decision 2026-10-24).</summary>
    public static class SealedDocumentFields
    {
        /// <summary>
        /// True when the author chose a neutral document name at filing; the real name lives inside the envelope, so
        /// the server never sees it. False (or absent) when the name stays readable, as any document's.
        /// </summary>
        public const string NameHidden = "Name hidden";
    }

    /// <summary>The CI/CD Secret mask's index fields (ADR 1084): all plain, so a dry run reads them without a key.</summary>
    public static class CiCdSecretFields
    {
        /// <summary>Where the entry is set: <c>github:owner/name</c>, or <c>github:owner/name@environment</c>. A list.</summary>
        public const string Targets = "Targets";

        /// <summary>The names of the sealed values (<c>MACOS_CERT_PASSWORD</c>). A list.</summary>
        public const string SecretKeys = "Secret keys";

        /// <summary>The secret keys whose value is a file: sealed as raw bytes, sent base64-encoded. A list.</summary>
        public const string FileKeys = "File keys";

        /// <summary>The variables, <c>NAME=value</c>: not secret, so not sealed. A list.</summary>
        public const string Variables = "Variables";

        /// <summary>As on a Password Entry: set by the server when a certificate it was sealed to is revoked.</summary>
        public const string ExposedSince = PasswordEntryFields.ExposedSince;
    }

    /// <summary>The Password Entry mask's index fields (ADR 0964, ADR 0973).</summary>
    public static class PasswordEntryFields
    {
        /// <summary>
        /// When a certificate the entry was sealed to was revoked: the entry's secret must be treated as exposed since
        /// then. Set by the server from the envelope's recipients; cleared only when a member files a NEW password,
        /// never by a re-seal, which keeps the old secret.
        /// </summary>
        public const string ExposedSince = "Exposed since";

        /// <summary>
        /// The site the entry belongs to (ADR 0973), the URL slot's host, lowercased: autofill matches a requesting
        /// browser or app by it WITHOUT opening the entry. Derived from the sealed content by the client that files or
        /// re-seals a version, never typed into a form of its own.
        /// </summary>
        public const string Site = "Site";

        /// <summary>
        /// The user name the entry fills (ADR 0973), from the content's user-name slot: the autofill sheet shows it
        /// before the key is used. Derived with <see cref="Site"/>.
        /// </summary>
        public const string UserName = "User name";

        /// <summary>
        /// An Android package name the entry also fills (ADR 0973), for an app with no verified link to its site: the
        /// one metadata value a user sets deliberately, as an ordinary field of the entry.
        /// </summary>
        public const string App = "App";

        /// <summary>
        /// Whether the entry's sealed content carries a verification code (ADR 1013): a Boolean, true or absent. It says
        /// only THAT the account uses one-time codes, never anything of the secret behind them, so a one-time-code
        /// field can be offered the entries that can fill it without opening any. Derived with <see cref="Site"/>.
        /// </summary>
        public const string HasCode = "Has code";

        /// <summary>
        /// The site the entry's passkey signs in to (ADR 1033): WebAuthn's relying-party id, lowercased. A sign-in names
        /// it, so the entries that can answer are found WITHOUT opening any. It can differ from <see cref="Site"/>, which is
        /// the URL's host. Derived with <see cref="Site"/>.
        /// </summary>
        public const string PasskeySite = "Passkey site";

        /// <summary>The passkey's credential id, base64url (ADR 1033): a site may ask for specific credentials by it, and
        /// a phone lists the passkey to its system by it. Derived with <see cref="Site"/>.</summary>
        public const string PasskeyId = "Passkey id";

        /// <summary>The site's opaque id for the account the passkey belongs to, base64url (ADR 1033): the phone's system
        /// lists a passkey only with it. Random and chosen by the site, not a secret. Derived with <see cref="Site"/>.</summary>
        public const string PasskeyUserHandle = "Passkey user handle";
    }
}
