using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Infrastructure.Search;

namespace Alcifra.IntegrationTests;

// A rebuild is a row (ADR 1081) because the installation runs two instances (ADR 0808): one is asked, either may be
// asked how it went. These pin the two properties that makes depend on: requests coalesce into the queued run, and
// of two instances woken for it only one claims it.
public class SearchReindexRunsTests
{
    private static async Task<(SqliteConnection Connection, Func<AlcifraDbContext> Open)> DatabaseAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        AlcifraDbContext Open() => new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());
        await using (var db = Open())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return (connection, Open);
    }

    [Fact]
    public async Task A_second_request_while_one_is_queued_is_the_same_run()
    {
        var (connection, open) = await DatabaseAsync();
        await using var _ = connection;
        await using var db = open();
        var now = DateTimeOffset.UtcNow;

        var first = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now, CancellationToken.None);
        var second = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now.AddSeconds(1), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.SearchReindexRuns.CountAsync());
    }

    [Fact]
    public async Task Of_two_instances_only_one_claims_the_run_and_a_new_request_then_queues_another()
    {
        var (connection, open) = await DatabaseAsync();
        await using var _ = connection;
        var now = DateTimeOffset.UtcNow;
        await using (var db = open())
        {
            await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now, CancellationToken.None);
        }

        await using var a = open();
        await using var b = open();
        var claimedByA = await SearchReindexRuns.ClaimNextAsync(a, "api", now, CancellationToken.None);
        var claimedByB = await SearchReindexRuns.ClaimNextAsync(b, "api-b", now, CancellationToken.None);

        Assert.NotNull(claimedByA);
        Assert.Equal("api", claimedByA.Instance);
        Assert.Null(claimedByB);

        // Once one is running, a request is a new run: it must not be satisfied by the one already under way.
        var next = await SearchReindexRuns.RequestAsync(b, SearchReindexTrigger.Requested, now.AddSeconds(1), CancellationToken.None);
        Assert.NotEqual(claimedByA.Id, next.Id);
    }

    [Fact]
    public async Task A_run_ends_with_its_count_or_its_reason()
    {
        var (connection, open) = await DatabaseAsync();
        await using var _ = connection;
        await using var db = open();
        var now = DateTimeOffset.UtcNow;

        var good = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now, CancellationToken.None);
        await SearchReindexRuns.ClaimNextAsync(db, "api", now, CancellationToken.None);
        await SearchReindexRuns.CountAttemptAsync(db, good.Id, CancellationToken.None);
        await SearchReindexRuns.SucceedAsync(db, good.Id, 42, now, CancellationToken.None);

        var bad = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now.AddSeconds(1), CancellationToken.None);
        await SearchReindexRuns.ClaimNextAsync(db, "api", now, CancellationToken.None);
        await SearchReindexRuns.FailAsync(db, bad.Id, new HttpRequestException("refused"), now.AddSeconds(2), CancellationToken.None);

        var succeeded = await SearchReindexRuns.FindAsync(db, good.Id, CancellationToken.None);
        Assert.Equal(42, succeeded!.IndexedCount);
        Assert.Equal(1, succeeded.Attempts);
        Assert.Null(succeeded.Failure);

        var failed = await SearchReindexRuns.FindAsync(db, bad.Id, CancellationToken.None);
        Assert.Equal("HttpRequestException: refused", failed!.Failure);
        Assert.NotNull(failed.FinishedAt);

        Assert.Equal(bad.Id, (await SearchReindexRuns.LatestAsync(db, CancellationToken.None))!.Id);
        Assert.Equal(42, await SearchReindexRuns.LastIndexedCountAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task The_prune_deletes_only_finished_runs_older_than_the_retention()
    {
        var (connection, open) = await DatabaseAsync();
        await using var _ = connection;
        await using var db = open();
        var longAgo = DateTimeOffset.UtcNow - SearchReindexRuns.Kept - TimeSpan.FromDays(1);

        var old = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, longAgo, CancellationToken.None);
        await SearchReindexRuns.ClaimNextAsync(db, "api", longAgo, CancellationToken.None);
        await SearchReindexRuns.SucceedAsync(db, old.Id, 1, longAgo, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var recent = await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Requested, now, CancellationToken.None);
        await SearchReindexRuns.ClaimNextAsync(db, "api", now, CancellationToken.None);
        await SearchReindexRuns.SucceedAsync(db, recent.Id, 2, now, CancellationToken.None);
        await SearchReindexRuns.PruneAsync(db, now, CancellationToken.None);

        Assert.Null(await SearchReindexRuns.FindAsync(db, old.Id, CancellationToken.None));
        Assert.NotNull(await SearchReindexRuns.FindAsync(db, recent.Id, CancellationToken.None));
    }
}
