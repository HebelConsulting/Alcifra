using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alcifra.Presentation;

/// <summary>The Tube tab's list as the module answers it (tube ADR 0007), read alike by every client (#2148).</summary>
public sealed class TubeShelfAnswer
{
    public bool EncryptsAtRest { get; set; }
    public List<TubeChannelRow> Items { get; set; } = [];
}

/// <summary>One channel; <see cref="CanWrite"/> false is the lock.</summary>
public sealed class TubeChannelRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool CanWrite { get; set; }
    public List<TubeVideoRow> Videos { get; set; } = [];
    public List<TubeLink> Links { get; set; } = [];

    /// <summary>The channel's own address for a rel; null means not available here (ADR 0543).</summary>
    public string? Href(string rel) => Links.FirstOrDefault(l => l.Rel == rel)?.Href;
}

/// <summary>One video in one channel, with what the person may do to it.</summary>
public sealed class TubeVideoRow
{
    public List<TubeLink> Links { get; set; } = [];

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool Published { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int DurationSeconds { get; set; }
    public Guid? ThumbnailId { get; set; }
    public bool IsReference { get; set; }
    public bool CanEdit { get; set; }
    public bool CanPlay { get; set; }
    public bool CanRemoveFromChannel { get; set; }
    public string? WatchUrl { get; set; }

    /// <summary>The row's own address for a rel; null means not available to this person, here, now (ADR 0543).</summary>
    public string? Href(string rel) => Links.FirstOrDefault(l => l.Rel == rel)?.Href;

    /// <summary>Only someone who may edit it ever sees a video unpublished, and it says so.</summary>
    public bool IsDraft => !Published;
}

/// <summary>A link on a row.</summary>
public sealed class TubeLink
{
    public string Rel { get; set; } = string.Empty;
    public string Href { get; set; } = string.Empty;
}

/// <summary>A short-lived address of a file, asked for when it is about to be shown.</summary>
public sealed class TubeContentLink
{
    public string Url { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>A row of the middle pane: a video in the channel it was listed under. A video in two ticked channels is two rows.</summary>
public sealed record TubeListing(TubeChannelRow Channel, TubeVideoRow Video);

/// <summary>What the Tube tab shows and sends: the rules every client must answer alike (#2148).</summary>
public static class TubeShelf
{
    /// <summary>The root rel the tab is offered by; no rel, no tab (ADR 0543).</summary>
    public const string Rel = "tube:channels";

    public const string PublishedField = "Published";
    public const string PublishedAtField = "Published at";
    public const string DescriptionField = "Description";
    public const string TagsField = "Tags";

    /// <summary>The videos of the ticked channels matching the filter, in the channels' order.</summary>
    public static IReadOnlyList<TubeListing> Visible(IEnumerable<TubeChannelRow> channels, ISet<Guid> ticked, string? filter)
    {
        var needle = filter?.Trim() ?? string.Empty;
        return
        [
            .. channels
                .Where(c => ticked.Contains(c.Id))
                .SelectMany(c => c.Videos.Where(v => Matches(v, needle)).Select(v => new TubeListing(c, v))),
        ];
    }

    private static bool Matches(TubeVideoRow video, string needle) =>
        needle.Length == 0
        || video.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
        || video.Description.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
        || video.Tags.Any(t => t.Contains(needle, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// The channels a video can still be placed in: those the person may write to that do not already hold it.
    /// </summary>
    public static IReadOnlyList<TubeChannelRow> PlaceableIn(IEnumerable<TubeChannelRow> channels, Guid videoId) =>
        [.. channels.Where(c => c.CanWrite && c.Videos.All(v => v.Id != videoId))];

    /// <summary>
    /// The full intended detail for a publish or an edit (ADRs 0794, 0872): what the form was filled from, echoed, with only
    /// the named fields changed. An omitted aspect would be a request to clear it. Publishing with no date stamps it, since
    /// the date orders the catalogue and nothing on the server may write it.
    /// </summary>
    /// <param name="baseline">The detail as read, whose tag the save is measured against.</param>
    /// <param name="fieldIds">The Video mask's field definitions, by name.</param>
    /// <param name="change">What changes.</param>
    /// <param name="now">When this is happening.</param>
    public static JsonObject DetailBody(JsonElement baseline, IReadOnlyDictionary<string, Guid> fieldIds, TubeVideoChange change, DateTimeOffset now)
    {
        var body = JsonNode.Parse(baseline.GetRawText())!.AsObject();
        foreach (var serverOnly in new[] { "links", "etag", "maskName", "sensitivityLabelName", "isFolder" })
        {
            body.Remove(serverOnly);
        }

        var values = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var group in baseline.GetProperty("fields").EnumerateArray())
        {
            values[group.GetProperty("fieldDefinitionId").GetGuid()] =
                [.. group.GetProperty("values").EnumerateArray().Select(v => v.GetString() ?? string.Empty)];
        }

        void Set(string field, IReadOnlyList<string> value)
        {
            if (fieldIds.TryGetValue(field, out var id))
            {
                values[id] = value;
            }
        }

        if (change.Published is { } published)
        {
            Set(PublishedField, [published ? "yes" : "no"]);
            if (published && (!fieldIds.TryGetValue(PublishedAtField, out var at) || !values.TryGetValue(at, out var stamp) || stamp.Count == 0))
            {
                Set(PublishedAtField, [IndexInstant.Store(now)]);
            }
        }

        if (change.Name is { Length: > 0 } name)
        {
            body["name"] = name;
        }

        if (change.Description is { } description)
        {
            Set(DescriptionField, [description]);
        }

        if (change.Tags is { } tags)
        {
            Set(TagsField, tags);
        }

        var fields = new JsonArray();
        foreach (var (id, list) in values)
        {
            var array = new JsonArray();
            foreach (var value in list)
            {
                array.Add(value);
            }

            fields.Add(new JsonObject { ["fieldDefinitionId"] = id.ToString(), ["values"] = array });
        }

        body["fields"] = fields;
        return body;
    }
}

/// <summary>What a save changes; a null member is left as it stands.</summary>
public sealed record TubeVideoChange(bool? Published = null, string? Name = null, string? Description = null, IReadOnlyList<string>? Tags = null);
