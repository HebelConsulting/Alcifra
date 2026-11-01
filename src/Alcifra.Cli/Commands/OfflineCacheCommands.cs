using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Shows what the tenant allows a phone's offline cache (#1742): followed from <c>/me</c>'s <c>offlineCachePolicy</c>.
/// </summary>
public sealed class OfflineCacheShowCommand(ILogger<OfflineCacheShowCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var (policy, _) = await OfflineCacheSurface.ReadAsync(new AlcifraApi(http), cancellationToken);
        logger.LogInformation("{Policy}", OfflineCacheSurface.Describe(policy));
        return 0;
    }
}

public sealed class OfflineCacheSetSettings : TenantSessionSettings
{
    [CommandOption("--allowed <true|false>")]
    [Description("The master switch: whether phones of this tenant keep an offline cache at all.")]
    public bool? Allowed { get; init; }

    [CommandOption("--notifications <true|false>")]
    [Description("Whether the cache may keep notifications.")]
    public bool? Notifications { get; init; }

    [CommandOption("--opened-documents <true|false>")]
    [Description("Whether the cache may keep the documents opened on the phone.")]
    public bool? OpenedDocuments { get; init; }

    [CommandOption("--chosen-folders <true|false>")]
    [Description("Whether the cache may keep the folders the user ticks.")]
    public bool? ChosenFolders { get; init; }

    [CommandOption("--unlock-mode <MODE>")]
    [Description("UserChooses, Session (key and PIN once) or Document (key and PIN for every document).")]
    public string? UnlockMode { get; init; }

    [CommandOption("--refresh-mode <MODE>")]
    [Description("UserChooses, KeepUpdated or RemoveWhenNewer.")]
    public string? RefreshMode { get; init; }

    [CommandOption("--max-unlocked-minutes <MINUTES>")]
    [Description("The longest an unlocked cache stays open in the background, 1 to 1440; the user may choose shorter.")]
    public int? MaxUnlockedMinutes { get; init; }
}

/// <summary>
/// Changes what the tenant allows a phone's offline cache (#1742; a tenant administrator). Options left out keep their
/// value: the policy is read first, changed, and written whole with the read's ETag as <c>If-Match</c>, so a colleague's
/// change in between is refused rather than overwritten (ADRs 0794, 0872).
/// </summary>
public sealed class OfflineCacheSetCommand(ILogger<OfflineCacheSetCommand> logger) : AsyncCommand<OfflineCacheSetSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, OfflineCacheSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var written = await OfflineCacheSurface.SetAsync(new AlcifraApi(http), settings, cancellationToken);
        logger.LogInformation("Offline-cache policy is now: {Policy}", OfflineCacheSurface.Describe(written));
        return 0;
    }
}

public static class OfflineCacheSurface
{
    public static async Task<(JsonElement Policy, string? ETag)> ReadAsync(AlcifraApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var me = await hypermedia.RootHrefAsync("me", cancellationToken);
        var href = Hypermedia.Href(await hypermedia.LinksOfAsync(me, cancellationToken), "offlineCachePolicy", "This installation");
        return await api.GetWithETagAsync(href, cancellationToken);
    }

    /// <summary>Reads the policy, applies the options given, and writes it whole with the read's ETag.</summary>
    public static async Task<JsonElement> SetAsync(AlcifraApi api, OfflineCacheSetSettings change, CancellationToken cancellationToken)
    {
        var (policy, etag) = await ReadAsync(api, cancellationToken);
        if (!policy.TryGetProperty("canEdit", out var canEdit) || canEdit.ValueKind != JsonValueKind.True)
        {
            throw new CliException("The offline-cache policy can be changed by a tenant administrator only.");
        }

        bool Flag(string name, bool? given) => given ?? policy.GetProperty(name).GetBoolean();
        return await api.PutWithETagAsync(Hypermedia.Href(Hypermedia.LinksOf(policy), "self", "The offline-cache policy"), new
        {
            allowed = Flag("allowed", change.Allowed),
            notifications = Flag("notifications", change.Notifications),
            openedDocuments = Flag("openedDocuments", change.OpenedDocuments),
            chosenFolders = Flag("chosenFolders", change.ChosenFolders),
            unlockMode = change.UnlockMode ?? policy.GetProperty("unlockMode").GetString(),
            refreshMode = change.RefreshMode ?? policy.GetProperty("refreshMode").GetString(),
            maxUnlockedMinutes = change.MaxUnlockedMinutes ?? policy.GetProperty("maxUnlockedMinutes").GetInt32(),
        }, etag, cancellationToken);
    }

    public static string Describe(JsonElement p) =>
        $"allowed {p.GetProperty("allowed").GetBoolean()}; notifications {p.GetProperty("notifications").GetBoolean()}; "
        + $"opened documents {p.GetProperty("openedDocuments").GetBoolean()}; chosen folders {p.GetProperty("chosenFolders").GetBoolean()}; "
        + $"unlock {p.GetProperty("unlockMode").GetString()}; refresh {p.GetProperty("refreshMode").GetString()}; "
        + $"background unlock up to {p.GetProperty("maxUnlockedMinutes").GetInt32()} min";
}
