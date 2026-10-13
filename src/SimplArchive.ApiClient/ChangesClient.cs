using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>
/// "Changes since" (core ADR 0925): what the caller may see that changed after a cursor, so an offline copy refreshes
/// only that (SimplArchiveMobile #5). Follows the root's <c>changes</c> rel; the cursor is opaque.
/// </summary>
public sealed class ChangesClient(ApiCore core)
{
    /// <summary>One changed document as the server answers it.</summary>
    public sealed record Change(Guid Id, Guid? ParentId, string Name, bool IsFolder, bool Deleted, Guid? CurrentVersionId,
        DateTimeOffset? VersionCreatedAt, DateTimeOffset ChangedAt);

    /// <summary>A page of changes and where the next call continues.</summary>
    public sealed record ChangePage(IReadOnlyList<Change> Changes, string Cursor, bool HasMore);

    /// <summary>Whether this installation answers "changes since" at all (older ones do not).</summary>
    public async Task<bool> IsOfferedAsync(CancellationToken cancellationToken = default) =>
        (await core.GetRootLinksAsync(cancellationToken)).ContainsKey("changes");

    /// <summary>
    /// A cursor at "now", taken BEFORE a copy is filled, so nothing that changes during the fill is missed.
    /// </summary>
    public async Task<string> StartAsync(CancellationToken cancellationToken = default) =>
        (await PageAsync(null, cancellationToken)).Cursor;

    /// <summary>Every change after <paramref name="since"/>, following the pages; returns them and the new cursor.</summary>
    public async Task<ChangePage> SinceAsync(string since, CancellationToken cancellationToken = default)
    {
        var all = new List<Change>();
        var cursor = since;
        while (true)
        {
            var page = await PageAsync(cursor, cancellationToken);
            all.AddRange(page.Changes);
            cursor = page.Cursor;
            if (!page.HasMore)
            {
                return new ChangePage(all, cursor, false);
            }
        }
    }

    private async Task<ChangePage> PageAsync(string? since, CancellationToken cancellationToken)
    {
        var href = await core.RootHrefAsync("changes", cancellationToken);
        var address = since is null ? $"{href}?limit=200" : $"{href}?since={Uri.EscapeDataString(since)}&limit=200";
        var json = await core.Http.GetFromJsonAsync<JsonElement>(address, cancellationToken);
        var changes = json.TryGetProperty("changes", out var rows)
            ? rows.EnumerateArray().Select(c => new Change(
                c.GetProperty("id").GetGuid(),
                c.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
                c.GetProperty("name").GetString() ?? string.Empty,
                c.GetProperty("isFolder").GetBoolean(),
                c.GetProperty("deleted").GetBoolean(),
                c.TryGetProperty("currentVersionId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetGuid() : null,
                c.TryGetProperty("versionCreatedAt", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : null,
                c.GetProperty("changedAt").GetDateTimeOffset())).ToList()
            : [];
        var hasMore = ApiCore.FindLink(json, "next") is not null;
        return new ChangePage(changes, json.GetProperty("cursor").GetString() ?? string.Empty, hasMore);
    }
}
