using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Client.Hypermedia;
using Alcifra.Presentation;

namespace Alcifra.Client.Services;

/// <summary>
/// The Tube tab's reads and writes (#2148), the web twin of the native clients' <c>TubeClient</c>. The list comes from the
/// module, by the person's own rights; every change goes through the core's own rels.
/// </summary>
public sealed class TubeService(HttpClient http, ApiRoot apiRoot, BrowseService browse)
{
    private IReadOnlyDictionary<string, Guid>? _videoFields;

    /// <summary>Whether the archive offers the tab here.</summary>
    public async Task<bool> IsOfferedAsync()
    {
        try
        {
            return await apiRoot.MemberHrefAsync(TubeShelf.Rel) is not null;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<TubeShelfAnswer> ReadAsync() =>
        await http.GetFromJsonAsync<TubeShelfAnswer>(await apiRoot.MemberHrefAsync(TubeShelf.Rel)
            ?? throw new InvalidOperationException($"The API root does not advertise '{TubeShelf.Rel}'.")) ?? new TubeShelfAnswer();

    /// <summary>A fresh short-lived address of a file behind a row's rel; null when it isn't available.</summary>
    public async Task<TubeContentLink?> LinkAsync(TubeVideoRow video, string rel)
    {
        if (video.Href(rel) is not { } href)
        {
            return null;
        }

        using var response = await http.GetAsync(href);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TubeContentLink>() : null;
    }

    /// <summary>Reads the video's detail for the pane; a save is measured against this read (ADR 0794).</summary>
    public async Task<TubeForm> OpenAsync(Guid videoId)
    {
        var document = await browse.FetchAsync(videoId);
        var href = Links.Href(document?.Links, "detail")
            ?? throw new InvalidOperationException($"Video {videoId} advertised no 'detail' rel (ADR 0543).");
        using var response = await http.GetAsync(href);
        response.EnsureSuccessStatusCode();
        return new TubeForm(href, await response.Content.ReadFromJsonAsync<JsonElement>(), response.Headers.ETag);
    }

    /// <summary>One save, one request; false means somebody else changed the video since it was read.</summary>
    public async Task<bool> SaveAsync(TubeForm form, TubeVideoChange change)
    {
        var fields = await VideoFieldsAsync(form.Baseline);
        using var request = new HttpRequestMessage(HttpMethod.Put, form.DetailHref)
        {
            Content = JsonContent.Create(TubeShelf.DetailBody(form.Baseline, fields, change, DateTimeOffset.Now)),
        };
        if (form.Etag is { } etag)
        {
            request.Headers.IfMatch.Add(etag);
        }

        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task PlaceAsync(Guid channelId, Guid videoId)
    {
        using var response = await http.PostAsJsonAsync(await browse.FetchRelAsync(channelId, "references"), new { targetId = videoId });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Takes a reference out of a channel. The video itself stays where it was prepared.</summary>
    public async Task RemoveFromChannelAsync(Guid channelId, Guid videoId)
    {
        var placed = await http.GetFromJsonAsync<ReferenceList>($"{await browse.FetchRelAsync(channelId, "references")}?limit=200");
        var href = placed?.References.FirstOrDefault(r => r.TargetId == videoId)?.Links.FirstOrDefault(l => l.Rel == "delete")?.Href
            ?? throw new InvalidOperationException("This video can't be removed from this channel.");
        using var response = await http.DeleteAsync(href);
        response.EnsureSuccessStatusCode();
    }

    // Structurally fixed, so asked once a session (ADR 0557); the mask is the one the video wears.
    private async Task<IReadOnlyDictionary<string, Guid>> VideoFieldsAsync(JsonElement detail)
    {
        if (_videoFields is null)
        {
            var maskId = detail.GetProperty("maskId").GetGuid();
            var list = await http.GetFromJsonAsync<JsonElement>(await apiRoot.RequireAsync("masks"));
            var self = list.GetProperty("masks").EnumerateArray()
                .First(m => m.GetProperty("id").GetGuid() == maskId)
                .GetProperty("links").EnumerateArray()
                .First(l => l.GetProperty("rel").GetString() == "self")
                .GetProperty("href").GetString()!;
            var mask = await http.GetFromJsonAsync<JsonElement>(self);
            _videoFields = mask.GetProperty("fields").EnumerateArray()
                .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("id").GetGuid(), StringComparer.Ordinal);
        }

        return _videoFields;
    }

    private sealed class ReferenceList
    {
        public List<ReferenceRow> References { get; set; } = [];
    }

    private sealed class ReferenceRow
    {
        public Guid TargetId { get; set; }
        public List<TubeLink> Links { get; set; } = [];
    }
}

/// <summary>A video's detail as read for the pane, with the tag a save of it is measured against.</summary>
public sealed record TubeForm(string DetailHref, JsonElement Baseline, System.Net.Http.Headers.EntityTagHeaderValue? Etag);
