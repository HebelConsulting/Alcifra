using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Startup;

/// <summary>
/// Serialises the startup seeding across instances, so two processes booting together cannot both insert the
/// same well-known row (#1295).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent is not the same as concurrency-safe, and that difference is the whole bug.</b> Every startup
/// seeder is idempotent in the sense of *safe to run twice*: check whether the row exists, insert it if not.
/// Run twice **at once** against an empty database, both processes find it absent, both insert, and the loser
/// gets
/// <code>23505: duplicate key value violates unique constraint "IX_OpenIddictApplications_ClientId"</code>
/// which reaches the <c>AppDomain.UnhandledException</c> backstop and terminates it.
/// </para>
/// <para>
/// <b>The damage is not the crash.</b> The process exits 0, <c>restart: unless-stopped</c> brings it back, and
/// the retry succeeds because the row now exists — so the app self-heals within seconds. What does not heal is
/// everything that was waiting on it: Docker Compose saw the api report unhealthy and abandoned its dependants,
/// which on the kiosk left the MTA unstarted and ingress mail silently down. And because the kiosk wipes and
/// reseeds nightly, the empty-database window this race needs came round **every night**.
/// </para>
/// <para>
/// <b>Why a lock and not a <c>23505</c> catch.</b> The race is not specific to OpenIddict: the well-known
/// masks, the module masks, the bootstrap administrator and both tenant seeders are all check-then-insert. A
/// lock fixes the region once instead of one constraint at a time, and it works where there is no "first
/// instance" to privilege — which is the Kubernetes case, since the chart runs two replicas by default and has
/// exactly this race on a first deploy.
/// </para>
/// <para>
/// <b>Why the connection is opened explicitly.</b> A session-level advisory lock belongs to its connection. Let
/// EF close the connection between commands — which it does by default — and the lock is released with it,
/// leaving a lock that looks taken and is not. So the connection is opened here and held until disposal.
/// </para>
/// <para>
/// <b>What happens if a process dies holding it:</b> nothing that needs cleaning up. Postgres releases a
/// session advisory lock when the connection drops, so the next instance acquires it normally.
/// </para>
/// </remarks>
public sealed class StartupSeedLock : IAsyncDisposable
{
    /// <summary>
    /// An arbitrary but FIXED key — every instance must choose the same number or they do not exclude each
    /// other. Derived from nothing, deliberately: a key computed from a name would invite someone to change
    /// the name and silently split the lock in two.
    /// </summary>
    private const long Key = 0x51_4D_50_4C_53_45_45_44; // "SMPLSEED"

    private readonly SimplArchiveDbContext? _dbContext;
    private readonly ILogger _logger;

    private StartupSeedLock(SimplArchiveDbContext? dbContext, ILogger logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>How long an instance waits for a sibling's seeding before it gives up (owner decision, #1773).</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DefaultPollEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Takes the lock, waiting for any other instance that is already seeding. A no-op on a provider without
    /// advisory locks (SQLite in tests), where there is only ever one process anyway.
    /// </summary>
    /// <remarks>
    /// <b>It polls rather than blocks</b> (#1773). A blocking <c>pg_advisory_lock</c> is one statement that lasts as long
    /// as the sibling seeds, so the command timeout (30 s) ended it while it was waiting correctly: the waiting instance
    /// died with exit 134 at the nightly reset and came back only through the restart policy. Each
    /// <c>pg_try_advisory_lock</c> returns at once, the wait is said in the log, and a sibling that never finishes ends
    /// in a named error after <paramref name="deadline"/> rather than a silent hang.
    /// </remarks>
    /// <exception cref="StartupSeedLockTimeoutException">The sibling held the lock for longer than the deadline.</exception>
    public static async Task<StartupSeedLock> AcquireAsync(SimplArchiveDbContext dbContext, ILogger logger,
        TimeSpan? deadline = null, TimeSpan? pollEvery = null)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            return new StartupSeedLock(null, logger);
        }

        var limit = deadline ?? DefaultDeadline;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        var waiting = false;
        while (!await TryTakeAsync(connection))
        {
            if (!waiting)
            {
                waiting = true;
                logger.LogInformation("Another instance is seeding the database; waiting for it to finish (up to {Deadline})", limit);
            }

            if (started.Elapsed >= limit)
            {
                await connection.CloseAsync();
                throw new StartupSeedLockTimeoutException(limit);
            }

            await Task.Delay(pollEvery ?? DefaultPollEvery);
        }

        if (waiting)
        {
            logger.LogInformation("The other instance finished seeding after {Waited}; seeding here", started.Elapsed);
        }

        logger.LogDebug("Startup seeding lock acquired; any sibling instance waits here until seeding completes.");
        return new StartupSeedLock(dbContext, logger);
    }

    // One immediate attempt on the held connection: true when this session now holds the lock.
    private static async Task<bool> TryTakeAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({Key})";
        return await command.ExecuteScalarAsync() is true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dbContext is null)
        {
            return;
        }

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", Key);
        await _dbContext.Database.GetDbConnection().CloseAsync();
        _logger.LogDebug("Startup seeding lock released.");
    }
}
