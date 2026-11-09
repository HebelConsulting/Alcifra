using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alcifra.Infrastructure.Search;

// Runs search-index rebuilds in the background (ADR 0139) — off the request path, one at a time. At startup
// it triggers an initial backfill if the alias doesn't exist yet (fresh deployment, or OpenSearch just
// enabled / its index lost); thereafter it processes admin-triggered requests. Each rebuild is a SearchReindexRun
// row (ADR 1081): the channel only wakes this instance, which then CLAIMS the oldest queued run, so whichever instance
// answers the next status request reports the same run. Registered only when OpenSearch is configured.
public sealed class SearchReindexService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchReindexState _state;
    private readonly ILogger<SearchReindexService> _logger;

    public SearchReindexService(IServiceScopeFactory scopeFactory, SearchReindexState state, ILogger<SearchReindexService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", nameof(SearchReindexService));
        await BackfillIfMissingAsync(stoppingToken);

        await foreach (var _ in _state.Requests.ReadAllAsync(stoppingToken))
        {
            // Every queued run, not one: requests that arrived while this one ran were coalesced into the channel.
            while (!stoppingToken.IsCancellationRequested && await ClaimAsync(stoppingToken) is { } run)
            {
                await RunWithRetryAsync(run.Id, stoppingToken);
            }
        }
    }

    private async Task<SearchReindexRun?> ClaimAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        return await SearchReindexRuns.ClaimNextAsync(db, Environment.MachineName, DateTimeOffset.UtcNow, stoppingToken);
    }

    // Queues a run of the service's own (the startup backfill) and wakes this instance.
    private async Task RequestAsync(CancellationToken stoppingToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            await SearchReindexRuns.RequestAsync(db, SearchReindexTrigger.Startup, DateTimeOffset.UtcNow, stoppingToken);
        }

        _state.Request();
    }

    /// <summary>
    /// Runs one rebuild, retrying with backoff for as long as the failure leaves search DEAD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a single attempt whose exception became one <c>LogWarning</c>. The request had already
    /// been taken from the (capacity-1) channel, so nothing retried, ever. When the failing attempt was the
    /// STARTUP backfill that consequence was total and permanent: the alias is never created, every
    /// per-document write is gated off waiting for it, and the process serves an empty search for the rest of
    /// its life — while answering every query with a cheerful zero hits.
    /// </para>
    /// <para>
    /// It is not hypothetical. A single <c>403</c> from OpenSearch on <c>CreateIndexAsync</c> wedged five CI
    /// legs exactly this way (#660/#661), and the symptom was a scattering of search tests timing out — which
    /// reads as flakiness, not as "search never started". A transient refusal must not be a permanent outage.
    /// </para>
    /// <para>
    /// The retry is bounded by CONSEQUENCE rather than by a count. While the alias is missing, search returns
    /// nothing and retrying forever is right. Once it exists, a failed rebuild is a degradation rather than an
    /// outage — the previous index is still serving — so it is reported and left for an administrator, who has
    /// the endpoint to retrigger it.
    /// </para>
    /// </remarks>
    private async Task RunWithRetryAsync(Guid runId, CancellationToken stoppingToken)
    {
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                _state.IsRunning = true;
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
                await SearchReindexRuns.CountAttemptAsync(db, runId, stoppingToken);
                var rebuilder = scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>();
                _state.LastIndexedCount = await rebuilder.RebuildAsync(stoppingToken);
                await SearchReindexRuns.SucceedAsync(db, runId, _state.LastIndexedCount, DateTimeOffset.UtcNow, stoppingToken);
                _logger.LogInformation("Rebuilt the search index with {Count} document(s).", _state.LastIndexedCount);
                await PruneAsync(stoppingToken);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (!await SearchIsDeadAsync(stoppingToken))
                {
                    await RecordFailureAsync(runId, e, stoppingToken);
                    // The existing index is still answering, so this is degraded rather than down.
                    _logger.LogWarning(
                        e,
                        "Search index rebuild failed on attempt {Attempt}. The existing index still serves queries, "
                        + "so results are STALE rather than absent; retrigger the rebuild once the cause is fixed. "
                        + "Set the Alcifra.Infrastructure.Search log level to Trace for the full exchange.",
                        attempt);
                    return;
                }

                // Named consequence, not just the failure: an administrator reading "rebuild failed" has no way
                // to know it means the whole search feature is returning nothing (ADR 0626).
                var delay = Backoff(attempt);
                _logger.LogWarning(
                    e,
                    "Search index rebuild failed on attempt {Attempt} and the '{Alias}' alias does not exist, so "
                    + "NOTHING is searchable until it succeeds — every query answers zero hits and every document "
                    + "write is queued. Retrying in {Delay}s. Set the Alcifra.Infrastructure.Search log level "
                    + "to Trace for the full exchange.",
                    attempt, OpenSearchIndexRebuilder.AliasName, delay.TotalSeconds);

                await Task.Delay(delay, stoppingToken);
            }
            finally
            {
                _state.IsRunning = false;
            }
        }
    }

    // A failure the service gives up on ends the run, so whoever follows it learns why. A failure it retries does not.
    private async Task RecordFailureAsync(Guid runId, Exception failure, CancellationToken stoppingToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            await SearchReindexRuns.FailAsync(db, runId, failure, DateTimeOffset.UtcNow, stoppingToken);
        }

        await PruneAsync(stoppingToken);
    }

    // Outside the run's outcome on purpose: the run has ended, and a prune that fails only leaves old rows a day longer.
    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await SearchReindexRuns.PruneAsync(scope.ServiceProvider.GetRequiredService<AlcifraDbContext>(), DateTimeOffset.UtcNow, stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not delete the search-index rebuilds older than {Days} days; they stay until the next rebuild.",
                SearchReindexRuns.Kept.TotalDays);
        }
    }

    /// <summary>Whether search currently returns nothing at all — i.e. the alias is absent.</summary>
    /// <remarks>An unreachable OpenSearch counts as dead: retrying is right in both cases.</remarks>
    private async Task<bool> SearchIsDeadAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var rebuilder = scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>();
            return !await rebuilder.AliasExistsAsync(stoppingToken);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>3 s doubling to a 60 s ceiling — quick enough for a transient refusal, quiet enough to live with.</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 3 * Math.Pow(2, Math.Min(attempt - 1, 5))));

    // Waits for OpenSearch to become reachable (it may still be starting), then triggers a backfill if the
    // alias is absent. A connection failure is a retry; a reachable "alias missing" enqueues the rebuild.
    private async Task BackfillIfMissingAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 20 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var rebuilder = scope.ServiceProvider.GetRequiredService<OpenSearchIndexRebuilder>();
                if (!await rebuilder.AliasExistsAsync(stoppingToken))
                {
                    _logger.LogInformation("Search alias missing at startup — running an initial backfill.");
                    await RequestAsync(stoppingToken);
                }
                else if (await rebuilder.MappingIsStaleAsync(stoppingToken))
                {
                    // An UPGRADE, not a first run: the alias exists and search works, but the live index
                    // predates a field the code now maps. Without this the field is absent for every document
                    // indexed before the deploy, and the queries using it answer correctly only for new ones —
                    // a half-right search being worse than an obviously broken one, since nobody investigates
                    // results that look plausible (#1254).
                    _logger.LogInformation(
                        "Search index predates the current mapping — rebuilding so every document carries the new fields.");
                    await RequestAsync(stoppingToken);
                }

                return; // OpenSearch answered — decision made
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        // Never reachable in 60 s. The old code gave up here and looked no further, which made a slow dependency
        // on a cold host indistinguishable from a permanent outage: no backfill, no alias, nothing searchable for
        // the life of the process. Request the rebuild anyway — it retries with backoff until it succeeds, and
        // an already-present alias makes it a cheap no-op rather than a mistake.
        _logger.LogWarning(
            "OpenSearch was not reachable within 60s of startup, so whether the index exists is unknown. Requesting "
            + "a backfill regardless; it retries until it succeeds. Until then search answers zero hits.");
        await RequestAsync(stoppingToken);
    }
}
