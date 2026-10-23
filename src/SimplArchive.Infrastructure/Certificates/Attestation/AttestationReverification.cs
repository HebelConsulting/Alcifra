using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Certificates.Attestation;

/// <summary>
/// Verifies every stored attestation again, across every tenant, and corrects each registry row's root (#1790, ADR 0963):
/// run when the installation gains a root, so evidence that chained to a root nobody knew becomes attested at once rather
/// than at the next start's rebuild. A switch needs no such run: the reader answer reads switches live.
/// </summary>
public static class AttestationReverification
{
    /// <summary>Returns how many registry rows changed.</summary>
    public static async Task<int> RunAsync(SimplArchiveDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Every tenant: a platform administrator's act, before any tenant is known (ADR 0206).
        var rows = await db.CertificateAttestations.IgnoreQueryFilters(["TenantFilter"]).AsNoTracking()
            .Join(db.EnrolledCertificates.IgnoreQueryFilters(["TenantFilter"]), a => a.DocumentId, c => c.DocumentId,
                (a, c) => new { Attestation = a, c.CertificatePem, c.AttestationRootThumbprint })
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var row in rows)
        {
            using var leaf = X509Certificate2.CreateFromPem(row.CertificatePem);
            var a = row.Attestation;
            var verdict = await AttestationTrust.VerifyAsync(db, new AttestationEvidence(a.Format, a.AttestationCertificatePem, a.IntermediateCertificatePem, a.Slot), leaf, cancellationToken);
            var root = verdict.Verified ? verdict.RootThumbprint : null;
            if (root == row.AttestationRootThumbprint)
            {
                continue;
            }

            // A machine-owned, untracked projection column (ADR 0935), so no token to move.
            await db.EnrolledCertificates.IgnoreQueryFilters(["TenantFilter"]).Where(c => c.DocumentId == a.DocumentId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AttestationRootThumbprint, root), cancellationToken);
            changed++;
        }

        if (changed > 0)
        {
            logger.LogInformation("Re-verified stored attestations: {Changed} of {Count} certificate(s) now chain to a different root", changed, rows.Count);
        }

        return changed;
    }
}
