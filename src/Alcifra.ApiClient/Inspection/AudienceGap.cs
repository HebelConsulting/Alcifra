using System.Security.Cryptography.X509Certificates;
using Alcifra.SealedArchive;

namespace Alcifra.ApiClient.Inspection;

/// <summary>
/// A certificate the installation knows (#2007): from its certificate listing, as the caller may see it (an administrator
/// every holder, a user their own). What a recipient reference is matched to when the caller is signed in.
/// </summary>
public sealed record KnownCertificate(
    string? Issuer, string? Serial, string? SubjectKeyIdentifier, string Label, string? HolderName, string? HolderEmail,
    DateTimeOffset? RevokedAt, string Attestation)
{
    /// <summary>Whether <paramref name="recipient"/> names this certificate, by the rule the server re-seals by.</summary>
    public bool Is(CmsRecipient recipient) =>
        (recipient.Serial is not null && recipient.Issuer is not null
            && string.Equals(recipient.Serial, Serial, StringComparison.OrdinalIgnoreCase)
            && string.Equals(recipient.Issuer, Issuer, StringComparison.Ordinal))
        || (recipient.SubjectKeyIdentifier is not null
            && string.Equals(recipient.SubjectKeyIdentifier, SubjectKeyIdentifier, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An audience member whose certificate the envelope does not name: the file needs re-sealing for them.</summary>
public sealed record MissingRecipient(string Person, X509Certificate2 Certificate);

/// <summary>
/// The gap between whom a document's content is sealed to and whom its declared audience says it should be (#2007, ADR
/// 0983): <see cref="Missing"/>, in the audience but not in the envelope (it needs a re-seal); <see cref="Extra"/>, in the
/// envelope but no longer in the audience (they can still open this version).
/// </summary>
public sealed record AudienceGap(IReadOnlyList<MissingRecipient> Missing, IReadOnlyList<InspectedRecipient> Extra)
{
    public bool None => Missing.Count == 0 && Extra.Count == 0;

    public static AudienceGap Of(IReadOnlyList<InspectedRecipient> envelope, ContentAudienceInfo audience)
    {
        var expected = audience.Recipients
            .SelectMany(r => r.CertificatePems.Select(pem => (Person: r.DisplayName, Certificate: X509Certificate2.CreateFromPem(pem))))
            .ToList();
        return new AudienceGap(
            [.. expected.Where(e => !envelope.Any(r => r.Reference.Is(e.Certificate))).Select(e => new MissingRecipient(e.Person, e.Certificate))],
            [.. envelope.Where(r => !expected.Any(e => r.Reference.Is(e.Certificate)))]);
    }
}
