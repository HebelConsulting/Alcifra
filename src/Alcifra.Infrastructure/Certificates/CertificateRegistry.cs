using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// The core's answer to "which certificates is this reader addressed by?" from its registry (#1790, ADR 0935):
/// moved from the Encryption Module's query, so the two agree while both exist. Revoked ones are left out in SQL;
/// the validity window in memory, because a DateTimeOffset comparison in a predicate throws at query time on SQLite
/// (the tests' provider), and the set is one reader's handful of keys.
/// </summary>
/// <remarks>
/// Attested enrolment (#1790, ADR 0963) narrows the answer further: a certificate addresses its holder only while its key
/// is attested to a root the installation trusts and the tenant has not switched off, unless the tenant allows hardware
/// without attestation. Asked here, at every answer, so switching a root or the tenant's setting takes effect at once and
/// never touches a certificate.
/// </remarks>
public static class CertificateRegistry
{
    /// <summary>The reader's usable certificates, longest-lived first (a stable order, not a precedence).</summary>
    public static async Task<IReadOnlyList<ReaderCertificate>> ForReaderAsync(
        AlcifraDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var enrolled = await db.EnrolledCertificates
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .Select(c => new { c.TenantId, c.CertificatePem, c.Label, c.NotBefore, c.NotAfter, c.Thumbprint, c.AttestationRootThumbprint })
            .ToListAsync(cancellationToken);
        if (enrolled.Count == 0)
        {
            return [];
        }

        var trust = await Attestation.AttestationTrust.ForTenantAsync(db, enrolled[0].TenantId, cancellationToken);
        return [.. enrolled
            .Where(c => c.NotBefore <= now && c.NotAfter > now)
            .Where(c => trust.Addresses(c.AttestationRootThumbprint))
            .OrderByDescending(c => c.NotAfter)
            .ThenBy(c => c.Thumbprint, StringComparer.Ordinal)
            .Select(c => new ReaderCertificate(c.CertificatePem, c.Label, c.NotAfter))];
    }
}
