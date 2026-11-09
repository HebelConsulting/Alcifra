using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Search;

// The rebuilds' record in the database (ADR 1081): how a request becomes a run, how an instance claims it and how it
// ends. The endpoint and SearchReindexService both go through here, so the two instances (ADR 0808) agree on it.
public static class SearchReindexRuns
{
    // Finished runs older than this are deleted when a later one finishes.
    public static readonly TimeSpan Kept = TimeSpan.FromDays(90);

    // Ordered IN MEMORY: SQLite, which the tests run on, refuses a DateTimeOffset ORDER BY (the SearchIndexWorker and
    // RepositoryExporter precedent). The table holds 90 days of rebuilds, a handful of rows.
    private static async Task<List<SearchReindexRun>> ByRequestAsync(
        IQueryable<SearchReindexRun> runs, CancellationToken cancellationToken) =>
        (await runs.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(r => r.RequestedAt).ThenBy(r => r.Id).ToList();

    /// <summary>
    /// The queued run if there is one (requests coalesce, as the channel always did), else a new one. The caller
    /// wakes its instance's worker afterwards.
    /// </summary>
    public static async Task<SearchReindexRun> RequestAsync(
        AlcifraDbContext db, SearchReindexTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = (await ByRequestAsync(db.SearchReindexRuns.Where(r => r.StartedAt == null), cancellationToken)).FirstOrDefault();
        if (queued is not null)
        {
            return queued;
        }

        var run = new SearchReindexRun { Id = Guid.NewGuid(), Trigger = trigger, RequestedAt = now };
        db.SearchReindexRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(run).State = EntityState.Detached;
        return run;
    }

    /// <summary>
    /// Claims the oldest queued run for this instance: one compare-and-swap on <c>StartedAt IS NULL</c> (ADR 0836), so
    /// of two instances woken for one request only one rebuilds. Null when there is nothing left to claim.
    /// </summary>
    public static async Task<SearchReindexRun?> ClaimNextAsync(
        AlcifraDbContext db, string instance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        while (true)
        {
            var candidate = (await ByRequestAsync(db.SearchReindexRuns.Where(r => r.StartedAt == null), cancellationToken))
                .Select(r => (Guid?)r.Id)
                .FirstOrDefault();
            if (candidate is not { } id)
            {
                return null;
            }

            var claimed = await db.SearchReindexRuns
                .Where(r => r.Id == id && r.StartedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.StartedAt, now).SetProperty(r => r.Instance, instance), cancellationToken);
            if (claimed == 1)
            {
                return await db.SearchReindexRuns.AsNoTracking().FirstAsync(r => r.Id == id, cancellationToken);
            }
        }
    }

    public static Task CountAttemptAsync(AlcifraDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.SearchReindexRuns.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Attempts, r => r.Attempts + 1), cancellationToken);

    public static Task SucceedAsync(
        AlcifraDbContext db, Guid id, int indexedCount, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.SearchReindexRuns.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FinishedAt, now).SetProperty(r => r.IndexedCount, indexedCount), cancellationToken);

    public static Task FailAsync(
        AlcifraDbContext db, Guid id, Exception failure, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reason = $"{failure.GetType().Name}: {failure.Message}";
        if (reason.Length > 2000)
        {
            reason = reason[..2000];
        }

        return db.SearchReindexRuns.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FinishedAt, now).SetProperty(r => r.Failure, reason), cancellationToken);
    }

    public static async Task<SearchReindexRun?> LatestAsync(AlcifraDbContext db, CancellationToken cancellationToken) =>
        (await ByRequestAsync(db.SearchReindexRuns, cancellationToken)).LastOrDefault();

    public static Task<SearchReindexRun?> FindAsync(AlcifraDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.SearchReindexRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    // Documents indexed by the latest run that succeeded, or null when none has.
    public static async Task<int?> LastIndexedCountAsync(AlcifraDbContext db, CancellationToken cancellationToken) =>
        (await ByRequestAsync(db.SearchReindexRuns.Where(r => r.IndexedCount != null), cancellationToken)).LastOrDefault()?.IndexedCount;

    /// <summary>
    /// Deletes the finished runs older than <see cref="Kept"/>. Its own step, after a run has ended: a failure here must
    /// never turn a rebuild that succeeded into one that failed. The old ones are picked in memory, as above.
    /// </summary>
    public static async Task PruneAsync(AlcifraDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - Kept;
        var old = (await db.SearchReindexRuns.AsNoTracking().Where(r => r.FinishedAt != null).ToListAsync(cancellationToken))
            .Where(r => r.FinishedAt < cutoff)
            .Select(r => r.Id)
            .ToList();
        if (old.Count > 0)
        {
            await db.SearchReindexRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
