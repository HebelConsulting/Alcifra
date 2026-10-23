using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Certificates;

/// <summary>
/// Keeps the core's certificate registry (<see cref="EnrolledCertificate"/>, #1790, ADR 0935) in step with one
/// Certificate document: moved from the Encryption Module's projection, run by the projection dispatcher inside the
/// document's own save transaction (ADR 0848), and by the startup rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the CONTENT for the certificate, and the document's fields only for what a person supplies</b> (Label,
/// Revoked on); the holder is the core's own Holder-to-user mapping (<c>ResourcePrincipals</c>, ADR 0851), never an
/// address parsed here.
/// </para>
/// <para>
/// <b>It never throws for a document it cannot use</b>, because it runs inside that document's save and a throw
/// would refuse the save itself: a certificate with no holder, unreadable content, or a key already enrolled under
/// another document leaves no row, with a log line saying why. A revocation, a deletion or a changed mask REMOVES
/// the row or marks it, in the same transaction, which is the direction that must never be missed.
/// </para>
/// </remarks>
public static class CertificateRegistryProjection
{
    /// <summary>Re-derives the registry row of <paramref name="documentId"/> in the current tenant.</summary>
    public static async Task ProjectAsync(
        SimplArchiveDbContext db, IObjectStorageClient storage, Guid documentId, Guid tenantId, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.EnrolledCertificates.SingleOrDefaultAsync(c => c.DocumentId == documentId, cancellationToken);
        var derived = await DeriveAsync(db, storage, documentId, tenantId, logger, cancellationToken);
        if (derived is null)
        {
            if (existing is not null)
            {
                db.EnrolledCertificates.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        // One enrolment per key and tenant: a second document carrying the same certificate is skipped, not thrown,
        // so filing it does not fail; the first enrolment stands.
        if (await db.EnrolledCertificates.AnyAsync(c => c.Thumbprint == derived.Thumbprint && c.DocumentId != documentId, cancellationToken))
        {
            logger.LogWarning(
                "Certificate document {DocumentId} carries a key already enrolled under another document; it is not registered again",
                documentId);
            return;
        }

        if (existing is null)
        {
            db.EnrolledCertificates.Add(derived);
        }
        else
        {
            (existing.UserId, existing.CertificatePem, existing.Label, existing.Thumbprint, existing.Serial,
                existing.NotBefore, existing.NotAfter, existing.RevokedAt, existing.AttestationRootThumbprint) =
                (derived.UserId, derived.CertificatePem, derived.Label, derived.Thumbprint, derived.Serial,
                    derived.NotBefore, derived.NotAfter, derived.RevokedAt, derived.AttestationRootThumbprint);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // The row a live, Certificate-masked document with a holder and a readable leaf projects; null otherwise.
    private static async Task<EnrolledCertificate?> DeriveAsync(
        SimplArchiveDbContext db, IObjectStorageClient storage, Guid documentId, Guid tenantId, ILogger logger,
        CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new
            {
                d.Id,
                d.CurrentVersionId,
                MaskId = db.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => v.MaskId).FirstOrDefault(),
                ParentMaskId = db.Documents.Where(p => p.Id == d.ParentId)
                    .Select(p => db.MaskVersions.Where(v => v.Id == p.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault())
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);   // soft-deleted documents are filtered out: no row for them
        if (document is null || document.MaskId != WellKnownMaskIds.Certificate)
        {
            return null;
        }

        // A READER certificate only (ADR 0936): the Certificate mask is shared, and a signing module's certificate or
        // a CA's kept for reference must never become an envelope recipient. Only the reader-certificate folder's
        // children are registered; a certificate moved out of it loses its row on the same save.
        if (document.ParentMaskId != WellKnownMaskIds.ReaderCertificates)
        {
            logger.LogDebug("Certificate document {DocumentId} is not in a reader-certificate folder; not registered", documentId);
            return null;
        }

        if (await db.ResourcePrincipals.Where(p => p.ResourceDocumentId == documentId).Select(p => (Guid?)p.UserId).FirstOrDefaultAsync(cancellationToken) is not { } userId)
        {
            // Legitimate (core ADR 0844): a certificate filed for somebody who is not a user here is addressed to nobody.
            logger.LogDebug("Certificate document {DocumentId} has no holder among this tenant's users; not registered", documentId);
            return null;
        }

        if (await CurrentVersion.ResolveAsync(db.DocumentVersions, documentId, document.CurrentVersionId, cancellationToken) is not { } version)
        {
            return null;
        }

        string pem;
        await using (var stream = await storage.GetObjectAsync(version.ObjectKey, cancellationToken))
        using (var reader = new StreamReader(stream))
        {
            pem = await reader.ReadToEndAsync(cancellationToken);
        }

        var chain = CertificateChain.Read(pem);
        if (!chain.Ok)
        {
            logger.LogWarning("Certificate document {DocumentId} is not registered: {Refusal}", documentId, chain.Refusal);
            return null;
        }

        var fields = await db.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(db.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
            .ToListAsync(cancellationToken);
        string Field(string name) => fields.FirstOrDefault(f => f.Name == name)?.Value ?? string.Empty;

        using var leaf = chain.Leaf!;
        return new EnrolledCertificate
        {
            DocumentId = documentId,
            TenantId = tenantId,
            UserId = userId,
            CertificatePem = leaf.ExportCertificatePem(),
            Label = Field("Label") is { Length: > 0 } label ? label : leaf.GetNameInfo(X509NameType.SimpleName, false),
            Thumbprint = Convert.ToHexString(SHA256.HashData(leaf.RawData)),
            // As X509Certificate2.SerialNumber renders it: one spelling, so a CA's revocation can join on it.
            Serial = leaf.SerialNumber.ToUpperInvariant(),
            // ToUniversalTime first: X509Certificate2 returns LOCAL times for values that are UTC in the certificate.
            NotBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            NotAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            // The one fact not in the certificate: a revocation a person records on the document.
            RevokedAt = DateTimeOffset.TryParse(Field("Revoked on"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var revoked) ? revoked : null,
            // Verified again from the stored evidence every time (ADR 0963): the evidence is the truth, this its reading.
            AttestationRootThumbprint = await Attestation.AttestationTrust.RootOfAsync(db, documentId, leaf, logger, cancellationToken),
        };
    }
}
