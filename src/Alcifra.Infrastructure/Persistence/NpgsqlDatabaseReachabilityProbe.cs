using Microsoft.Extensions.Logging;
using Npgsql;
using Alcifra.Application.Abstractions;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// Opens a genuinely new physical connection to Postgres and classifies the outcome by SQLSTATE (#1287,
/// ADR 0807).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own data source, with pooling OFF.</b> The comment on the app's single data source warns that a
/// second live one would silently double the connection ceiling (ADR 0701, #750) — which is exactly right, and
/// is why this one keeps no pool at all. It retains nothing between checks: at most one connection exists, for
/// as long as the check runs, and the ceiling applied to the app's pool is untouched. The one honest cost is
/// that during a saturation the probe asks for a slot too; that is the price of asking the question that
/// matters, and the answer it gets back (<c>53300</c>) is the one the caller needs.
/// </para>
/// <para>
/// <b>The same password provider as the app.</b> The whole point is to present the credential the app would
/// present. Reading the connection string's own password instead would make the probe green while the app
/// could not connect, which is the defect this class exists to remove — a different version of the same lie.
/// </para>
/// <para>
/// <b>A short connect timeout, deliberately.</b> Probes are budgeted 3 seconds (the chart's
/// <c>timeoutSeconds</c> and the Dockerfile's <c>--timeout</c>). Npgsql's default is 15, so a slow failure
/// would blow the probe's budget and be recorded as a timeout — losing the classification this class exists to
/// produce. Two seconds leaves the classification inside the budget.
/// </para>
/// </remarks>
public sealed class NpgsqlDatabaseReachabilityProbe : IDatabaseReachabilityProbe, IAsyncDisposable
{
    private const int ConnectTimeoutSeconds = 2;

    /// <summary>
    /// Opening a local connection takes a few milliseconds; from this long it is logged (#2344), well before the
    /// two-second limit turns it into a failed probe. On 2026-11-16 the kiosk's passing probes took ~1.35 s just
    /// before failing ones, and nothing recorded it.
    /// </summary>
    public static readonly TimeSpan SlowOpenWarning = TimeSpan.FromMilliseconds(500);

    private readonly NpgsqlDataSource _dataSource;
    private readonly RefreshableDatabasePassword _password;
    private readonly ILogger _logger;

    public NpgsqlDatabaseReachabilityProbe(
        string connectionString, RefreshableDatabasePassword password, ILogger logger)
    {
        _password = password;
        _logger = logger;

        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = ConnectTimeoutSeconds,
        }.ConnectionString);

        if (password.IsActive)
        {
            builder.UsePasswordProvider(
                _ => password.GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                (_, cancellationToken) => password.GetAsync(cancellationToken));
        }

        _dataSource = builder.Build();
    }

    public async ValueTask<DatabaseReachability> CheckAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            _logger.LogTrace("Readiness probe opened a new physical connection to the database.");
            WarnIfSlow(_logger, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return DatabaseReachability.Reachable;
        }
        catch (Exception e)
        {
            return Classify(e);
        }
    }

    private DatabaseReachability Classify(Exception e)
    {
        var sqlState = (e as PostgresException ?? e.InnerException as PostgresException)?.SqlState;

        switch (sqlState)
        {
            case PostgresErrorCodes.InvalidPassword:
                // Tell the password holder, so an idle-hours rotation is repaired before the first real
                // request meets it — the probe as earliest detector rather than last to know.
                //
                // Best-effort by construction: Postgres returns 28P01 for an unknown role as well as a wrong
                // password (it will not let a caller enumerate usernames), so this may be asking the secrets
                // store about a role that no longer exists. That costs one store read per probe tick while the
                // condition lasts; a floor on forced re-reads was considered and deliberately not added, since
                // the condition is a hard outage somebody must fix and the extra reads are cheap beside it.
                if (_password.IsActive)
                {
                    _password.Invalidate();
                }

                _logger.LogWarning(
                    "Readiness: the database refused the runtime credential ({SqlState}). Readiness is now "
                    + "reporting unhealthy so this instance is taken out of rotation. Note that Postgres "
                    + "returns this code for an unknown role as well as a wrong password.", sqlState);
                return new DatabaseReachability(DatabaseReachabilityState.CredentialRefused, sqlState);

            case PostgresErrorCodes.TooManyConnections:
                // DEGRADED, not unhealthy, and the distinction is the point of this class. Pooled connections
                // still work, so every replica failing readiness at once would pull a Service that is still
                // serving most of its traffic — turning a shortage into an outage.
                _logger.LogWarning(
                    "Readiness: the database is at a connection limit ({SqlState}). Reporting degraded rather "
                    + "than unhealthy — connections already held keep working, so taking this instance out of "
                    + "rotation would remove capacity that is still serving.", sqlState);
                return new DatabaseReachability(DatabaseReachabilityState.Saturated, sqlState);

            default:
                _logger.LogWarning(e,
                    "Readiness: a new database connection could not be opened ({SqlState}).",
                    sqlState ?? "no SQLSTATE — the server did not answer");
                return new DatabaseReachability(DatabaseReachabilityState.Unreachable, sqlState);
        }
    }

    /// <summary>Logs a probe whose connection opened, but slowly: the early sign of a stall (#2344).</summary>
    public static void WarnIfSlow(ILogger logger, TimeSpan elapsed)
    {
        if (elapsed >= SlowOpenWarning)
        {
            logger.LogWarning(
                "Readiness: opening a new database connection took {ElapsedMs} ms (warning from {WarnMs} ms, "
                + "failing at {LimitS} s).",
                (int)elapsed.TotalMilliseconds, (int)SlowOpenWarning.TotalMilliseconds, ConnectTimeoutSeconds);
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
