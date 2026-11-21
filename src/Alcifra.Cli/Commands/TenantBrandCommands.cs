using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Shows the tenant's runtime brand (ADR 1103): the pack it wears after sign-in, the palette in force and whether
/// users may choose one for themselves.
/// </summary>
/// <remarks>
/// Followed, never composed (ADR 0543): the root's <c>tenants</c> collection is the platform administrator's, and each
/// tenant row carries its own <c>brand</c> rel.
/// </remarks>
public sealed class TenantBrandShowCommand(ILogger<TenantBrandShowCommand> logger) : AsyncCommand<TenantBrandShowSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantBrandShowSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var (brand, _) = await TenantBrandSurface.ReadAsync(api, settings.Tenant, cancellationToken);
        logger.LogInformation("{Brand}", TenantBrandSurface.Describe(brand));
        return 0;
    }
}

public sealed class TenantBrandShowSettings : PlatformAdminSettings
{
    [CommandArgument(0, "<tenant-id>")]
    [Description("The tenant whose brand to show.")]
    public required Guid Tenant { get; init; }
}

public sealed class TenantBrandSetSettings : PlatformAdminSettings
{
    [CommandArgument(0, "<tenant-id>")]
    [Description("The tenant to assign the brand to.")]
    public required Guid Tenant { get; init; }

    [CommandOption("--pack <PACK-ID|none>")]
    [Description("The runtime brand pack to wear, or 'none' to return the tenant to the installation's brand.")]
    public string? Pack { get; init; }

    [CommandOption("--palette <PALETTE-ID|none>")]
    [Description("The tenant's default palette within that pack, or 'none' for the pack's own colours.")]
    public string? Palette { get; init; }

    [CommandOption("--allow-user-override <true|false>")]
    [Description("Whether users may pick another palette of the same brand in self-service.")]
    public bool? AllowUserOverride { get; init; }
}

/// <summary>
/// Assigns a tenant's runtime brand (ADR 1103) — a platform administrator's act, and an administrative one, so it is
/// done here rather than with a hand-written request (ADR 0871).
/// </summary>
/// <remarks>
/// Options left out keep their value: the resource is read first, for its own values and its ETag, and written whole
/// with that ETag as <c>If-Match</c> (ADRs 0794, 0872) — so a colleague's assignment in between is refused rather
/// than overwritten.
/// </remarks>
public sealed class TenantBrandSetCommand(ILogger<TenantBrandSetCommand> logger) : AsyncCommand<TenantBrandSetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantBrandSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);
        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        var written = await TenantBrandSurface.SetAsync(
            api, settings.Tenant, settings.Pack, settings.Palette, settings.AllowUserOverride, cancellationToken);

        logger.LogInformation("Brand is now: {Brand}", TenantBrandSurface.Describe(written));
        return 0;
    }
}

public static class TenantBrandSurface
{
    internal const string Rel = "brand";

    /// <summary>The tenant's brand resource and its ETag — the read half of the read-modify-write.</summary>
    public static async Task<(JsonElement Brand, string? ETag)> ReadAsync(
        AlcifraApi api, Guid tenantId, CancellationToken cancellationToken) =>
        await api.GetWithETagAsync(await HrefAsync(api, tenantId, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes the brand whole: options left as <c>null</c> keep the value the resource already carries; the literal
    /// <c>none</c> clears a pack or a palette. Clearing the pack clears the palette with it — a palette belongs to a
    /// pack, and storing one without its pack would leave a preference nothing can resolve.
    /// </summary>
    public static async Task<JsonElement> SetAsync(
        AlcifraApi api,
        Guid tenantId,
        string? pack,
        string? palette,
        bool? allowUserOverride,
        CancellationToken cancellationToken)
    {
        var (brand, etag) = await ReadAsync(api, tenantId, cancellationToken);

        var packId = pack is null ? StringValue(brand, "packId") : NoneIsNull(pack);
        var paletteId = palette is null ? StringValue(brand, "paletteId") : NoneIsNull(palette);
        if (packId is null)
        {
            paletteId = null;
        }

        var allow = allowUserOverride ?? brand.GetProperty("allowUserPaletteOverride").GetBoolean();

        // The write's address and verb both come from the resource the server advertised (ADR 0543).
        var assign = Hypermedia.Href(Hypermedia.LinksOf(brand), "assign", "The tenant's brand");
        return await api.PutWithETagAsync(
            assign,
            new { packId, paletteId, allowUserPaletteOverride = allow },
            etag,
            cancellationToken);
    }

    public static string Describe(JsonElement brand)
    {
        var pack = StringValue(brand, "packId") ?? "none (the installation's brand)";
        var palette = StringValue(brand, "paletteId") ?? "the pack's own colours";
        var selfService = brand.GetProperty("allowUserPaletteOverride").GetBoolean()
            ? "users may choose a palette"
            : "the tenant's palette only";

        var available = brand.GetProperty("availablePackIds").EnumerateArray()
            .Select(candidate => candidate.GetString())
            .Where(candidate => candidate is { Length: > 0 })
            .ToList();

        return $"pack {pack}; palette {palette}; {selfService}; "
            + $"this installation carries: {(available.Count == 0 ? "no packs" : string.Join(", ", available))}";
    }

    private static string? NoneIsNull(string value) =>
        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static string? StringValue(JsonElement resource, string property) =>
        resource.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Root `tenants` (a platform administrator's) → the row of this tenant → its own `brand` rel. Paged, because the
    // collection is cursor-paged (ADR 0207) and a tenant late in the list is still a tenant.
    private static async Task<string> HrefAsync(AlcifraApi api, Guid tenantId, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        string? page = await hypermedia.RootHrefAsync("tenants", cancellationToken);
        while (page is not null)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var tenant in listing.GetProperty("tenants").EnumerateArray())
            {
                if (tenant.GetProperty("id").GetGuid() == tenantId)
                {
                    return Hypermedia.Href(Hypermedia.LinksOf(tenant), Rel, $"Tenant {tenantId}");
                }
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        throw new CliException($"No tenant {tenantId} was listed for this platform administrator.");
    }
}
