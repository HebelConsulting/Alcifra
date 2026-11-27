using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// The ONE place a document's next version number is computed (ADR 1113): the number and the read that decides it are
/// taken under a per-document advisory lock, because two finalizes of one document otherwise both read "highest = N"
/// and both write N+1.
/// </summary>
/// <remarks>
/// <para>
/// The unique index on <c>(DocumentId, VersionNumber)</c> is the backstop that turns a missed lock into a failed write
/// instead of a silently ambiguous history — it does not replace the lock, and the lock does not replace it.
/// </para>
/// <para>
/// <b>Callers must already have a transaction in flight.</b> The advisory lock is transaction-scoped: taken outside a
/// transaction it protects nothing (it is released when the implicit single statement ends). The four allocation sites
/// each own or join a unit of work — <c>DocumentFinalizer.FileAsync</c>, the searchable-PDF worker's per-item
/// transaction, the module facade's write, and the repository importer's import transaction. The guard below refuses
/// rather than computing a number that nothing protects.
/// </para>
/// </remarks>
public static class DocumentVersionNumbers
{
    /// <summary>
    /// The next number for <paramref name="documentId"/>: one past the highest CONFIRMED number, read under the
    /// document's advisory lock. Pending versions carry no number (<see cref="Domain.Documents.DocumentVersion"/>) and
    /// do not participate.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is in flight (see the remarks).</exception>
    public static async Task<int> NextAsync(AlcifraDbContext dbContext, Guid documentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Postgres only: SQLite (the tests' provider) has one connection and nothing to contend with — the same
        // decision as the module escalation claim (ADR 0836).
        if (dbContext.Database.IsNpgsql())
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException(
                    "DocumentVersionNumbers.NextAsync needs a transaction in flight: its advisory lock is released at the "
                    + "transaction's end, so a number computed outside one would protect nothing. Open the unit of work "
                    + "first (FileAsync, or a conditionally-owned transaction as ADR 0781 describes).");
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({LockKey(documentId)})", cancellationToken);
        }

        var highest = await dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.VersionNumber != null)
            .Select(v => v.VersionNumber)
            .MaxAsync(cancellationToken);
        return 1 + (highest ?? 0);
    }

    /// <summary>
    /// A stable per-document lock key: the first 8 bytes of SHA-256 over a fixed prefix and the id — stable across
    /// instances and restarts, unlike <see cref="object.GetHashCode"/>, and distinct from the escalation sweep's keys.
    /// </summary>
    public static long LockKey(Guid documentId) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"document-version|{documentId:D}")), 0);
}
