using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Rebuilds the search index (#1715, ADR 1081), a platform-administrator act: it spans every tenant. Until this the
/// only way was a hand-written POST to the root's <c>searchReindex</c> rel, which ADR 0871 forbids.
/// </summary>
/// <remarks>
/// <c>--wait</c> follows the run the POST answered, by its own <c>self</c> link, until it ends. The run lives in the
/// database, so it is answered the same by whichever instance the proxy picks.
/// </remarks>
public sealed class SearchReindexCommand(ILogger<SearchReindexCommand> logger) : AsyncCommand<SearchReindexCommand.Settings>
{
    public sealed class Settings : PlatformAdminSettings
    {
        [CommandOption("--wait")]
        [Description("Wait until the rebuild has finished, and fail if it failed.")]
        public bool Wait { get; init; }

        [CommandOption("--timeout <MINUTES>")]
        [Description("How long --wait waits before giving up (default 30).")]
        public int Timeout { get; init; } = 30;

        public override ValidationResult Validate() => this switch
        {
            _ when base.Validate() is { Successful: false } failed => failed,
            { Timeout: < 1 } => ValidationResult.Error("--timeout must be at least 1 minute."),
            _ => ValidationResult.Success(),
        };
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var run = await SearchReindexSurface.RequestAsync(api, cancellationToken);
        logger.LogInformation("Search index rebuild {Run}: {Status}.", SearchReindexSurface.Text(run, "runId"), SearchReindexSurface.Text(run, "status"));
        if (!settings.Wait)
        {
            return 0;
        }

        var done = await SearchReindexSurface.FollowAsync(
            api, run, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(settings.Timeout), TimeSpan.FromSeconds(3),
            status => logger.LogInformation("Search index rebuild: {Status}.", status), cancellationToken);
        logger.LogInformation("Rebuilt the search index with {Count} document(s).", done.GetProperty("lastIndexedCount").GetInt32());
        return 0;
    }
}

/// <summary>The latest search-index rebuild and the index's health (ADR 1081), for a platform administrator.</summary>
public sealed class SearchReindexStatusCommand(ILogger<SearchReindexStatusCommand> logger) : AsyncCommand<PlatformAdminSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PlatformAdminSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var status = await SearchReindexSurface.StatusAsync(api, cancellationToken);
        logger.LogInformation(
            "Latest rebuild: {Status}{Run}. Last indexed: {Count} document(s). Alias missing: {AliasMissing}. Pending in the outbox: {Pending}.",
            SearchReindexSurface.Text(status, "status"),
            SearchReindexSurface.Text(status, "runId") is { Length: > 0 } run ? $" ({run})" : string.Empty,
            status.GetProperty("lastIndexedCount").GetInt32(),
            status.TryGetProperty("aliasMissing", out var missing) && missing.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? missing.GetBoolean() ? "yes" : "no"
                : "unknown",
            status.GetProperty("pendingOutbox").GetInt32());
        if (SearchReindexSurface.Text(status, "failure") is { Length: > 0 } failure)
        {
            logger.LogWarning("The latest rebuild failed: {Failure}", failure);
        }

        return 0;
    }
}
