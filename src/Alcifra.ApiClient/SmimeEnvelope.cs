using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using MimeKit;
using MimeKit.Cryptography;

namespace Alcifra.ApiClient;

/// <summary>
/// The two MIME steps around opening a strict-tier envelope, shared by every client (ADR 0917): a served
/// <c>application/pkcs7-mime</c> response is an S/MIME message whose body carries the CMS envelope, and what the
/// key decrypts is the MIME part the server enveloped, which names the document's real content type.
/// </summary>
/// <remarks>
/// Only the KEY differs between hosts (the desktop's certificate store or card, the phone's hardware key over NFC or
/// USB), so only the step between these two is theirs. Moved here from the desktop rather than repeated on the
/// phone: two readings of one wire format is how a producer and a consumer come to disagree about it.
/// </remarks>
public static class SmimeEnvelope
{
    /// <summary>The S/MIME part a served envelope arrives as.</summary>
    /// <exception cref="EnvelopeNotOpenedException">The response is labelled as an envelope but holds none.</exception>
    public static ApplicationPkcs7Mime Unwrap(byte[] served)
    {
        try
        {
            if (MimeMessage.Load(new MemoryStream(served)).Body is ApplicationPkcs7Mime enveloped)
            {
                return enveloped;
            }
        }
        catch (FormatException)
        {
        }

        // A SERVED ENVELOPE MAY BE THE BARE CMS ITSELF (ADR 0975). A sealed document is stored as bare
        // EnvelopedData — the core's shape check reads the DER header at the door, so an S/MIME wrapper would be
        // refused there — and a strict tenant hands a shaped sealed object through unchanged. Wrapping it here
        // keeps ONE opener for both producers, instead of a second reading of the wire format in every host.
        if (IsBareEnvelopedData(served))
        {
            return new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(served));
        }

        // Labelled as an envelope and shaped like something else. Refused rather than guessed at: handing the
        // caller bytes we could not open would make a decryption failure look like a corrupt document.
        throw new EnvelopeNotOpenedException("the response was labelled as an envelope but does not contain one");
    }

    /// <summary>Whether these bytes decode as a bare CMS <c>EnvelopedData</c> structure.</summary>
    private static bool IsBareEnvelopedData(byte[] bytes)
    {
        try
        {
            new EnvelopedCms().Decode(bytes);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>The bare CMS <c>EnvelopedData</c> inside the S/MIME part; empty when it carries no content.</summary>
    public static byte[] Cms(ApplicationPkcs7Mime enveloped)
    {
        if (enveloped.Content is null)
        {
            return [];
        }

        using var cms = new MemoryStream();
        enveloped.Content.DecodeTo(cms);
        return cms.ToArray();
    }

    /// <summary>The CMS envelope of a served response, for a host that opens CMS itself (the phone).</summary>
    public static byte[] Cms(byte[] served) => Cms(Unwrap(served));

    /// <summary>The document a key decrypted, and its real content type, from the inner MIME bytes.</summary>
    public static (byte[] Bytes, string ContentType) Payload(byte[] decrypted) =>
        Payload(MimeEntity.Load(new MemoryStream(decrypted)));

    /// <summary>The document and its real content type, from the decrypted MIME entity.</summary>
    /// <remarks>
    /// The INNER type is what a caller renders: the outer pkcs7-mime describes only the wrapper. A MULTIPART is handed on
    /// whole, headers and all, typed as itself: a password entry is one (its fields, and <c>passwd.txt</c>, ADR 0964), and
    /// its reader parses the parts.
    /// </remarks>
    public static (byte[] Bytes, string ContentType) Payload(MimeEntity decrypted)
    {
        if (decrypted is Multipart multipart)
        {
            using var whole = new MemoryStream();
            multipart.WriteTo(whole);
            return (whole.ToArray(), multipart.ContentType.MimeType);
        }

        if (decrypted is not MimePart part || part.Content is null)
        {
            throw new EnvelopeNotOpenedException("the envelope opened but held no document");
        }

        using var opened = new MemoryStream();
        part.Content.DecodeTo(opened);
        return (opened.ToArray(), part.ContentType?.MimeType ?? "application/octet-stream");
    }
}

/// <summary>The envelope could not be opened — never a fall back to whatever arrived.</summary>
/// <remarks>
/// Its own type because the remedy differs from every other download failure: no key at hand means enrol or present
/// the key, not retry. Serving the caller the undecrypted bytes would render as a corrupt document and send them
/// looking for a damaged file.
/// <para>
/// Thrown as itself for an envelope that is damaged or otherwise unreadable (a real failure). An envelope left sealed
/// because no key of the user's opened it is <see cref="EnvelopeStaysSealedException"/>, carrying the state a preview
/// shows instead of an error (ADR 0969).
/// </para>
/// </remarks>
public class EnvelopeNotOpenedException(string message, Exception? inner = null)
    : Exception($"This document arrived sealed and could not be opened: {message}.", inner);

/// <summary>A refusal that carries the state a preview shows for it (ADR 0969, <see cref="SealedContent.OutcomeOf"/>).</summary>
public interface ISealedRefusal
{
    SealedOutcome Outcome { get; }
}

/// <summary>
/// No key of the signed-in user's opened the envelope (ADR 0969): none at hand, a key at hand not enrolled for the user,
/// the user's keys unknown, or the envelope sealed for other keys — which of them is <see cref="Outcome"/>.
/// </summary>
public sealed class EnvelopeStaysSealedException(string message, SealedOutcome outcome)
    : EnvelopeNotOpenedException(message), ISealedRefusal
{
    public SealedOutcome Outcome { get; } = outcome;
}

/// <summary>
/// The key prompt was cancelled — the user's decision, not a failure. An <see cref="OperationCanceledException"/>, as the
/// hosts' callers expect of a cancelled prompt (ADR 0967), carrying what the preview then shows.
/// </summary>
public sealed class KeyPromptCancelledException(SealedOutcome outcome)
    : OperationCanceledException("The key prompt was cancelled."), ISealedRefusal
{
    public SealedOutcome Outcome { get; } = outcome;
}
