using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Counts, per tenant, the content of masks that declare a content segment and sits OUTSIDE it (#2304): the read-only
/// audit for the one-key-factory rule and the mask-change move (ADR 0937). A platform-administrator act; changes
/// nothing.
/// </summary>
/// <remarks>
/// What it finds is history, not a live defect: the versions filed by a door that built the archive key itself, or
/// given a segmented mask after filing. Playback is unaffected (a document presigns by document), but the segment's
/// promise — one prefix per module for delete-by-prefix, backup and lifecycle; for a sealed mask, the storage door that
/// knows content only by its key — does not hold for those bytes. Nothing here repairs them: it reports.
/// </remarks>
public sealed class StorageCheckCommand(ILogger<StorageCheckCommand> logger) : AsyncCommand<StorageCheckCommand.Settings>
{
    public const string Rel = "storage-out-of-segment";

    public sealed class Settings : PlatformAdminSettings
    {
        public override ValidationResult Validate() => base.Validate();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var tenants = 0;
        var flagged = 0;
        string? page = await new Hypermedia(api).RootHrefAsync("tenants", cancellationToken);
        while (page is not null)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var tenant in listing.GetProperty("tenants").EnumerateArray())
            {
                // A server from before the check exists advertises no rel: it has nothing to answer, which reads
                // exactly like the "checked, nothing found" case but is not one — say so instead of passing silently.
                if (!Hypermedia.LinksOf(tenant).TryGetValue(Rel, out var href))
                {
                    logger.LogInformation(
                        "This installation does not answer the storage check (no '{Rel}' rel): it predates it, or this is not "
                        + "a platform-administrator session.", Rel);
                    return 0;
                }

                tenants++;
                var name = tenant.GetProperty("name").GetString();
                var answer = await api.GetAsync(href, cancellationToken);
                foreach (var segment in answer.GetProperty("segments").EnumerateArray())
                {
                    var segmentName = segment.GetProperty("segment").GetString();
                    var total = segment.GetProperty("versions").GetInt32();
                    var inside = segment.GetProperty("inSegment").GetInt32();
                    var outside = segment.GetProperty("outOfSegment").GetInt32();
                    if (outside > 0)
                    {
                        flagged++;
                        logger.LogWarning(
                            "{Tenant}: segment '{Segment}' holds {Outside} version(s) OUTSIDE it ({Inside} inside, {Total} in total). "
                            + "Those bytes predate the one key factory or a mask change's move: they work, but the segment's "
                            + "prefix, backup and lifecycle do not cover them.",
                            name, segmentName, outside, inside, total);
                    }
                    else if (total > 0)
                    {
                        logger.LogInformation(
                            "{Tenant}: segment '{Segment}' is clean ({Inside} version(s), all inside).", name, segmentName, inside);
                    }
                }
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        // The verdict line: what an operator pastes into a ticket, and what a re-run compares against.
        if (flagged > 0)
        {
            logger.LogInformation(
                "Checked {Tenants} tenant(s): {Flagged} segment(s) hold content outside where it belongs. See the warnings above.",
                tenants, flagged);
        }
        else
        {
            logger.LogInformation("Checked {Tenants} tenant(s): every declared content segment holds its content.", tenants);
        }

        return 0;
    }
}
