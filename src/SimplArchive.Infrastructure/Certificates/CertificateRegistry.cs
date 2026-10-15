using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Certificates;

/// <summary>
/// The core's answer to "which certificates is this reader addressed by?" from its registry (#1790, ADR 0935):
/// moved from the Encryption Module's query, so the two agree while both exist. Revoked ones are left out in SQL;
/// the validity window in memory, because a DateTimeOffset comparison in a predicate throws at query time on SQLite
/// (the tests' provider), and the set is one reader's handful of keys.
/// </summary>
public static class CertificateRegistry
{
    /// <summary>The reader's usable certificates, longest-lived first (a stable order, not a precedence).</summary>
    public static async Task<IReadOnlyList<ReaderCertificate>> ForReaderAsync(
        SimplArchiveDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var enrolled = await db.EnrolledCertificates
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .Select(c => new { c.CertificatePem, c.Label, c.NotBefore, c.NotAfter, c.Thumbprint })
            .ToListAsync(cancellationToken);

        return [.. enrolled
            .Where(c => c.NotBefore <= now && c.NotAfter > now)
            .OrderByDescending(c => c.NotAfter)
            .ThenBy(c => c.Thumbprint, StringComparer.Ordinal)
            .Select(c => new ReaderCertificate(c.CertificatePem, c.Label, c.NotAfter))];
    }
}
