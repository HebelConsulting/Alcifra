using System.Security.Cryptography.X509Certificates;
using MimeKit;
using SimplArchive.Messenger.Client;

namespace SimplArchive.ApiClient;

/// <summary>
/// A sealed document's content, sealed ON THE DEVICE (ADR 0975): the ACL says who may SEE the document, this envelope
/// says who may READ it, and the server never holds a key for either.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is a MIME part, not the bare file.</b> What a key decrypts must name the document's real content
/// type — the same reason the strict tier's server envelops a part (ADR 0828) — so the opener's
/// <see cref="SmimeEnvelope.Payload(byte[])"/> can hand a caller bytes and their type. A bare <c>byte[]</c> would
/// come back as whatever the MIME reader guessed.
/// </para>
/// <para>
/// <b>The envelope is BARE CMS</b>, not an S/MIME message, because the core's shape check reads the DER header
/// (<c>CmsEnvelopeShape</c>) at the door: an S/MIME message begins with MIME headers and is refused there. The
/// funnel wraps a bare envelope in memory when it opens one, so both producers reach the same opener.
/// </para>
/// <para>
/// <b>One sealer.</b> The CMS is built by the Messenger client's <see cref="ConversationKeys.SealTo"/> — RSA key
/// transport or elliptic-curve key agreement, whichever a recipient's certificate calls for — the same call the
/// password store, the offline archive and the hardware test use. A second CMS writer is how two producers come to
/// disagree about a format.
/// </para>
/// </remarks>
public static class SealedDocument
{
    /// <summary>
    /// The version's extension and the requested <c>fileExtension</c>: an envelope is not a PDF, and the server names the
    /// stored object after what was filed.
    /// </summary>
    public const string FileExtension = ".p7m";

    /// <summary>
    /// The well-known mask a sealed document wears. Mirrors <c>SimplArchive.Domain.Masks.WellKnownMaskIds.SealedDocument</c>
    /// — the CLIENT cannot reference the server's Domain, and every tenant reuses the well-known ids (ADR 0198).
    /// <c>SealedDocumentClientTests</c> asserts the two agree, so a drift fails the build rather than the feature.
    /// </summary>
    public static readonly Guid MaskId = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E50");

    /// <summary>The "Re-sealed after an access change" version comment kind (ADR 0964), as the wire carries it.</summary>
    public const int ResealedAfterAccessChange = 2;

    /// <summary>
    /// The bare CMS envelope of <paramref name="content"/>, sealed to every given certificate.
    /// </summary>
    /// <param name="content">The document's bytes.</param>
    /// <param name="contentType">The document's real content type, carried INSIDE the envelope.</param>
    /// <param name="recipients">The audience: one certificate per reader. Never empty — an envelope nobody opens is not a seal.</param>
    /// <param name="fileName">
    /// The document's name, where it was filed under a neutral one ("Name hidden"): it rides in the part's
    /// <c>Content-Disposition</c>, inside the envelope, so only the audience reads it.
    /// </param>
    /// <exception cref="SealedDocumentAudienceRequiredException">No recipient was chosen.</exception>
    public static byte[] Seal(
        byte[] content, string contentType, IReadOnlyList<X509Certificate2> recipients, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (recipients.Count == 0)
        {
            throw new SealedDocumentAudienceRequiredException();
        }

        var part = new MimePart(contentType)
        {
            Content = new MimeContent(new MemoryStream(content)),
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        if (fileName is { Length: > 0 })
        {
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName };
        }

        using var payload = new MemoryStream();
        part.WriteTo(payload);
        return ConversationKeys.SealTo(payload.ToArray(), recipients);
    }
}

/// <summary>A sealed document was about to be filed with no audience: nobody could open it.</summary>
public sealed class SealedDocumentAudienceRequiredException()
    : ApiActionException("Choose at least one person or certificate to seal this document to.");
