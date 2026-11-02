using System.Security.Cryptography.X509Certificates;
using Alcifra.SealedArchive;

namespace Alcifra.ApiClient.Inspection;

/// <summary>What kind of sealed thing a file is (#2007).</summary>
public enum SealedKind
{
    /// <summary>A CMS <c>EnvelopedData</c>: content encrypted to its recipients (a content envelope, a password entry, a
    /// Sealed document, a Messenger file).</summary>
    Enveloped,

    /// <summary>A CMS <c>AuthEnvelopedData</c> (RFC 5083): the same, with authenticated encryption.</summary>
    AuthEnveloped,

    /// <summary>A CMS <c>SignedData</c>: content (or a detached signature over it) with its signers.</summary>
    Signed,

    /// <summary>A sealed archive (ADR 0961): a signed plain cover, and a payload key sealed to its recipients.</summary>
    Archive,

    /// <summary>None of the above.</summary>
    NotSealed,
}

/// <summary>One recipient an envelope names, as it names it: a certificate reference, never a person.</summary>
/// <param name="Method">How the content key reaches it: <c>key transport</c> (RSA) or <c>key agreement</c> (ECDH).</param>
/// <param name="KeyEncryptionAlgorithm">The algorithm wrapping the content key for it (an OID, with its name where known).</param>
/// <param name="OriginatorKeyAlgorithm">Key agreement only: the algorithm of the sender's ephemeral key.</param>
public sealed record InspectedRecipient(CmsRecipient Reference, string Method, string KeyEncryptionAlgorithm, string? OriginatorKeyAlgorithm);

/// <summary>One signer of a <c>SignedData</c>.</summary>
/// <param name="SignatureValid">Whether its signature verifies over the content the file carries; null for a detached
/// signature, which needs the signed file to be checked.</param>
public sealed record InspectedSigner(string? Subject, string? Issuer, string? Serial, string DigestAlgorithm, DateTimeOffset? SigningTime, bool? SignatureValid);

/// <summary>A sealed archive's cover as it reads, and whether its signature holds (not whether its signer is trusted:
/// trust is the reader's, ADR 0961).</summary>
public sealed record InspectedArchive(
    string Name, DateTimeOffset ExportedAt, string Source, IReadOnlyList<ArchiveRecipientLabel> Labels, bool SignatureValid, string? Signer);

/// <summary>
/// What a sealed file is and who it names (#2007), read without any key: the shape, the algorithms, the recipients by
/// certificate reference, the signers, and an archive's cover. Matching a reference to a person is a separate step
/// (<see cref="RecipientMatching"/>).
/// </summary>
/// <param name="SmimeWrapped">The CMS arrived inside an S/MIME message (as the server serves an envelope).</param>
/// <param name="ContentType">The inner content's type (an OID, with its name where known).</param>
/// <param name="ContentEncryption">The content encryption algorithm of an envelope.</param>
/// <param name="Certificates">The certificates a <c>SignedData</c> carries, by subject.</param>
/// <param name="Cms">The CMS structure itself, for the ASN.1 tree; null when there is none.</param>
public sealed record SealedInspection(
    SealedKind Kind,
    bool SmimeWrapped,
    long Size,
    string? ContentType,
    string? ContentEncryption,
    IReadOnlyList<InspectedRecipient> Recipients,
    IReadOnlyList<InspectedSigner> Signers,
    IReadOnlyList<string> Certificates,
    InspectedArchive? Archive,
    byte[]? Cms)
{
    /// <summary>The format's name, as a person reads it.</summary>
    public string Format => Kind switch
    {
        SealedKind.Enveloped => "CMS EnvelopedData",
        SealedKind.AuthEnveloped => "CMS AuthEnvelopedData",
        SealedKind.Signed => "CMS SignedData",
        SealedKind.Archive => "Sealed archive",
        _ => "Not a sealed file",
    };
}

/// <summary>A recipient, and the certificate it was matched to (null: an unknown recipient).</summary>
public sealed record MatchedRecipient(InspectedRecipient Recipient, X509Certificate2? Certificate);

/// <summary>Matches an envelope's recipients to certificates (#2007): by issuer and serial number, or by subject key
/// identifier, the same rule the server uses to say an entry needs re-sealing (<see cref="CmsRecipient.Is"/>).</summary>
public static class RecipientMatching
{
    public static IReadOnlyList<MatchedRecipient> Match(IReadOnlyList<InspectedRecipient> recipients, IReadOnlyList<X509Certificate2> certificates) =>
        [.. recipients.Select(r => new MatchedRecipient(r, certificates.FirstOrDefault(r.Reference.Is)))];
}
