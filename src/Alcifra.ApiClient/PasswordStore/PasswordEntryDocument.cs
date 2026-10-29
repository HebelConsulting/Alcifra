namespace Alcifra.ApiClient.PasswordStore;

/// <summary>
/// A Password Entry met as a DOCUMENT (Mobile ADR 0037; the desktop's Repositories tree since ADR 0968): in a repository
/// tree and in an offline copy, where the password store's own rels do not exist (ADR 0931: entries ride the copy exactly
/// as stored, sealed). Its content is read through the host's content funnel like any document's and opened with the key
/// on every read, then shown as an entry, read-only: changing it is the Passwords screen's, online.
/// </summary>
public static class PasswordEntryDocument
{
    /// <summary>The seeded mask's name (<c>WellKnownMaskSeeder</c>), which a listing row and the copy carry.</summary>
    public const string MaskName = "Password Entry";

    private const string EnvelopeType = "application/pkcs7-mime; smime-type=enveloped-data";

    /// <summary>Whether a document of this mask is a password entry.</summary>
    public static bool Is(string? maskName) => string.Equals(maskName, MaskName, StringComparison.Ordinal);

    /// <summary>
    /// The entry from what the funnel returned: stored bytes are a BARE envelope (served as plain bytes), which is opened
    /// here as the shared client does; bytes the funnel already opened (a tier that delivered them in an envelope of its
    /// own) are the entry, or once more the bare envelope inside.
    /// </summary>
    /// <exception cref="PasswordEntryUnreadableException">What opened is not a password entry.</exception>
    public static async Task<PasswordEntryContent> ParseAsync(byte[] bytes, bool openedByFunnel, IEnvelopeOpener opener)
    {
        if (openedByFunnel && !LooksLikeEnvelope(bytes))
        {
            return PasswordEntryFormat.Parse(bytes);
        }

        var (opened, _) = await opener.OpenAsync(PasswordStoreClient.AsSmime(bytes), EnvelopeType);
        return PasswordEntryFormat.Parse(opened);
    }

    // DER: a CMS ContentInfo is a SEQUENCE; a MIME entity starts with a header line.
    private static bool LooksLikeEnvelope(byte[] bytes) => bytes.Length > 1 && bytes[0] == 0x30;
}
