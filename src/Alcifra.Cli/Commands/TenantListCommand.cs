using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Names every tenant on the installation, by id and name (a platform administrator's act).
/// </summary>
/// <remarks>
/// The read half of a RE-RUNNABLE seed: provisioning a tenant is not find-or-create, so a seed that must not
/// duplicate its tenants on a second run asks this first. Followed from the root's <c>tenants</c> rel (ADR 0543),
/// across every page.
/// </remarks>
public sealed class TenantListCommand(ILogger<TenantListCommand> logger) : AsyncCommand<PlatformAdminSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PlatformAdminSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var tenants = await TenantListSurface.ReadAsync(api, cancellationToken);
        foreach (var (id, name) in tenants)
        {
            // Data output (ADR 0906): id<TAB>name per line on stdout, for a seed's lookup — nothing else goes there.
            Console.Out.WriteLine($"{id}\t{name}");
        }

        // The human-facing summary goes through the logger, like every other line this tool prints for a person.
        logger.LogInformation("Listed {Count} tenant(s).", tenants.Count);
        return 0;
    }
}

public static class TenantListSurface
{
    /// <summary>Every tenant, in the collection's order, across all cursor pages (ADR 0207).</summary>
    public static async Task<IReadOnlyList<(Guid Id, string Name)>> ReadAsync(
        AlcifraApi api, CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, string Name)>();
        string? page = await new Hypermedia(api).RootHrefAsync("tenants", cancellationToken);
        while (page is not null)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var tenant in listing.GetProperty("tenants").EnumerateArray())
            {
                rows.Add((
                    tenant.GetProperty("id").GetGuid(),
                    tenant.GetProperty("name").GetString() ?? string.Empty));
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        return rows;
    }
}
