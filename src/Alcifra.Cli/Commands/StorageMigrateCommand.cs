using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Moves every tenant's objects from its bucket under the legacy prefix to the one under the current prefix (ADR 1000),
/// a platform-administrator act: one batch per request, each reported as it lands (ADR 0999).
/// </summary>
/// <remarks>
/// Stops anywhere and runs again from the start: every step is idempotent. A legacy copy a retention date or legal
/// hold still keeps stays until a later run finds its lock expired.
/// </remarks>
public sealed class StorageMigrateCommand(ILogger<StorageMigrateCommand> logger) : AsyncCommand<StorageMigrateCommand.Settings>
{
    public const string Rel = "storage-migration";

    public sealed class Settings : PlatformAdminSettings
    {
        [CommandOption("--batch <N>")]
        [Description("Objects per request (1–200, default 50).")]
        public int? Batch { get; init; }

        public override ValidationResult Validate() => this switch
        {
            _ when base.Validate() is { Successful: false } failed => failed,
            { Batch: < 1 or > 200 } => ValidationResult.Error("--batch must be between 1 and 200."),
            _ => ValidationResult.Success(),
        };
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var total = new Totals();
        var tenants = 0;
        string? page = await new Hypermedia(api).RootHrefAsync("tenants", cancellationToken);
        while (page is not null)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var tenant in listing.GetProperty("tenants").EnumerateArray())
            {
                // The rel is advertised only while ObjectStorage:LegacyBucketPrefix is set: without it there is nothing to move.
                if (!Hypermedia.LinksOf(tenant).TryGetValue(Rel, out var href))
                {
                    logger.LogInformation("This installation has no legacy bucket prefix configured: there is nothing to migrate.");
                    return 0;
                }

                tenants++;
                total.Add(await MigrateTenantAsync(api, tenant, href, settings.Batch, cancellationToken));
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        if (total.Retained > 0)
        {
            logger.LogInformation(
                "Done with {Tenants} tenant(s): {Copied} copied, {Removed} legacy copies removed, {Retained} kept by a lock until {Until:yyyy-MM-dd} "
                + "at the latest. Every object is readable from the new buckets; run this again after that date to remove the rest, and "
                + "keep ObjectStorage:LegacyBucketPrefix set until it reports nothing kept.",
                tenants, total.Copied, total.Removed, total.Retained, total.RetainedUntil);
        }
        else
        {
            logger.LogInformation(
                "Done with {Tenants} tenant(s): {Copied} copied, {Removed} legacy copies removed, nothing left. "
                + "ObjectStorage:LegacyBucketPrefix can now be removed.",
                tenants, total.Copied, total.Removed);
        }

        return 0;
    }

    private async Task<Totals> MigrateTenantAsync(AlcifraApi api, JsonElement tenant, string href, int? batchSize, CancellationToken cancellationToken)
    {
        var name = tenant.GetProperty("name").GetString();
        var totals = new Totals();
        string? next = batchSize is { } size ? $"{href}?limit={size}" : href;
        while (next is not null)
        {
            var batch = await api.PostAsync(next, new { }, cancellationToken);
            totals.Add(batch);
            logger.LogInformation(
                "{Tenant}: {Copied} copied, {Present} already there, {Removed} removed, {Retained} kept by a lock (so far {Total} done).",
                name, batch.GetProperty("copied").GetInt32(), batch.GetProperty("alreadyPresent").GetInt32(),
                batch.GetProperty("removed").GetInt32(), batch.GetProperty("retained").GetInt32(), totals.Copied + totals.Present);
            next = Hypermedia.LinksOf(batch).GetValueOrDefault("next");
        }

        return totals;
    }

    private sealed class Totals
    {
        public int Copied { get; private set; }

        public int Present { get; private set; }

        public int Removed { get; private set; }

        public int Retained { get; private set; }

        public DateTimeOffset? RetainedUntil { get; private set; }

        public void Add(JsonElement batch)
        {
            Copied += batch.GetProperty("copied").GetInt32();
            Present += batch.GetProperty("alreadyPresent").GetInt32();
            Removed += batch.GetProperty("removed").GetInt32();
            Retained += batch.GetProperty("retained").GetInt32();
            if (batch.TryGetProperty("retainedUntil", out var until) && until.ValueKind == JsonValueKind.String)
            {
                Later(until.GetDateTimeOffset());
            }
        }

        public void Add(Totals other)
        {
            Copied += other.Copied;
            Present += other.Present;
            Removed += other.Removed;
            Retained += other.Retained;
            if (other.RetainedUntil is { } until)
            {
                Later(until);
            }
        }

        private void Later(DateTimeOffset until) => RetainedUntil = RetainedUntil is { } known && known >= until ? known : until;
    }
}
