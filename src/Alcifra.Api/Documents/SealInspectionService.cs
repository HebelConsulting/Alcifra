using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Audience;
using Alcifra.Api.Certificates;
using Alcifra.Api.Errors.Exceptions.Inspection;
using Alcifra.ApiClient.Inspection;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Persistence;
using Alcifra.ModuleAbi;

namespace Alcifra.Api.Documents;

/// <summary>
/// Answers a document's seal inspection (#2291): its current version read as stored, never decrypted, by the one reader the
/// CLI uses (<see cref="SealedInspector"/>, ADR 1029), its recipients matched to the installation's certificates under the
/// certificate listing's own rights, and compared with the declared audience.
/// </summary>
/// <remarks>
/// The bytes stay on the server: the storage read decrypts the at-rest layer only, never the seal, and only the description
/// leaves. Offered on a document whose content is sealed to an audience (a Sealed document, a store's entry).
/// </remarks>
public sealed class SealInspectionService(
    AlcifraDbContext dbContext,
    IObjectStorageClient storage,
    ContentAudience audience,
    CertificateEnrolment enrolment)
{
    /// <summary>What is read whole into memory at most; a larger envelope is inspected with <c>alcifra inspect</c>.</summary>
    public const long MaxInspectedBytes = 64L * 1024 * 1024;

    public async Task<SealInspectionResource> InspectAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var root = await audience.RootOfAsync(documentId, cancellationToken) ?? throw new NothingSealedToInspectException();
        var pointer = await dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(cancellationToken);
        var version = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, documentId, pointer, cancellationToken)
            ?? throw new NothingSealedToInspectException();
        var size = await storage.GetObjectSizeAsync(version.ObjectKey, cancellationToken);
        if (size > MaxInspectedBytes)
        {
            throw new SealTooLargeToInspectException(MaxInspectedBytes);
        }

        byte[] bytes;
        await using (var source = await storage.GetObjectAsync(version.ObjectKey, cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var inspection = SealedInspector.Inspect(bytes);
        var known = await enrolment.ListAsync(null, cancellationToken);
        var members = await audience.EffectiveAsync(root, cancellationToken);
        var gap = AudienceGap.Of(inspection.Recipients, [.. members.Select(m => (m.DisplayName, m.CertificatePems))]);
        return Resource(documentId, inspection, known, gap);
    }

    private static SealInspectionResource Resource(
        Guid documentId, SealedInspection inspection, IReadOnlyList<CertificateEnrolment.Listed> known, AudienceGap gap) => new()
        {
            Kind = inspection.Kind.ToString(),
            Format = inspection.Format,
            SmimeWrapped = inspection.SmimeWrapped,
            Size = inspection.Size,
            ContentType = inspection.ContentType,
            ContentEncryption = inspection.ContentEncryption,
            Recipients = [.. inspection.Recipients.Select(r => Recipient(r, known.FirstOrDefault(k => Names(k, r)), !gap.Extra.Contains(r)))],
            Signers = [.. inspection.Signers.Select(s => new SealSignerResource
            {
                Subject = s.Subject,
                Issuer = s.Issuer,
                Serial = s.Serial,
                DigestAlgorithm = s.DigestAlgorithm,
                SigningTime = s.SigningTime,
                SignatureValid = s.SignatureValid,
            })],
            Certificates = [.. inspection.Certificates],
            Audience = new SealAudienceResource
            {
                Matches = gap.None,
                Missing = [.. gap.Missing.Select(m => new SealMissingResource { Person = m.Person, Thumbprint = m.Certificate.Thumbprint })],
            },
            Asn1 = inspection.Cms is { } cms
                ? [.. Asn1Tree.Of(cms).Select(n => new SealAsn1NodeResource
                {
                    Offset = n.Offset,
                    Depth = n.Depth,
                    HeaderLength = n.HeaderLength,
                    Length = n.Length,
                    Constructed = n.Constructed,
                    Tag = n.Tag,
                    Value = n.Value,
                })]
                : [],
            Links = [new Link("self", $"/api/documents/{documentId}/seal-inspection", "GET")],
        };

    private static SealRecipientResource Recipient(InspectedRecipient r, CertificateEnrolment.Listed? k, bool inAudience) => new()
    {
        HolderName = k?.HolderName,
        HolderEmail = k?.HolderEmail,
        Label = k?.Label,
        RevokedAt = k?.RevokedAt,
        Attestation = k?.Attestation,
        NotAfter = k?.NotAfter,
        Issuer = r.Reference.Issuer,
        Serial = r.Reference.Serial,
        SubjectKeyIdentifier = r.Reference.SubjectKeyIdentifier,
        Method = r.Method,
        KeyEncryptionAlgorithm = r.KeyEncryptionAlgorithm,
        OriginatorKeyAlgorithm = r.OriginatorKeyAlgorithm,
        InAudience = inAudience,
    };

    // The rule the CLI matches the installation's certificates by (KnownCertificate.Is), over the listing's own rows.
    private static bool Names(CertificateEnrolment.Listed k, InspectedRecipient r) =>
        new KnownCertificate(k.Issuer, k.Serial, k.SubjectKeyIdentifier, k.Label, k.HolderName, k.HolderEmail, k.RevokedAt, k.Attestation)
            .Is(r.Reference);
}
