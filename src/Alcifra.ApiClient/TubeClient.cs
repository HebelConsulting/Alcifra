using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Presentation;

namespace Alcifra.ApiClient;

/// <summary>
/// The Tube tab's client (#2148), shared by the desktop and the phone. The list comes from the module, by the person's own
/// rights; every change goes through the core's own rels, so the same rights and refusals apply as anywhere else.
/// </summary>
public sealed class TubeClient(ApiCore core, DocumentsClient documents, MasksClient masks, ReferencesClient references)
{
    private IReadOnlyDictionary<string, Guid>? _videoFields;

    /// <summary>Whether the archive offers the tab here.</summary>
    public async Task<bool> IsOfferedAsync(CancellationToken cancellationToken = default) =>
        (await core.GetRootLinksAsync(cancellationToken)).ContainsKey(TubeShelf.Rel);

    public async Task<TubeShelfAnswer> ReadAsync(CancellationToken cancellationToken = default) =>
        await core.Http.GetFromJsonAsync<TubeShelfAnswer>(await core.RootHrefAsync(TubeShelf.Rel, cancellationToken), cancellationToken)
        ?? new TubeShelfAnswer();

    /// <summary>The picture of a video, or null when it has none or it can't be read: a row is complete without one.</summary>
    public async Task<byte[]?> ThumbnailAsync(TubeVideoRow video, CancellationToken cancellationToken = default)
    {
        if (video.Href("thumbnail") is not { } href)
        {
            return null;
        }

        using var response = await core.Http.GetAsync(href, cancellationToken);
        if (!response.IsSuccessStatusCode || await response.Content.ReadFromJsonAsync<TubeContentLink>(cancellationToken) is not { } link)
        {
            return null;
        }

        return (await ApiCore.GetContentAsync(link.Url, cancellationToken)).Bytes;
    }

    /// <summary>A fresh address of the video's file, or null when it can't be played here.</summary>
    public async Task<TubeContentLink?> PlayLinkAsync(TubeVideoRow video, CancellationToken cancellationToken = default)
    {
        if (video.Href("play") is not { } href)
        {
            return null;
        }

        using var response = await core.Http.GetAsync(href, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TubeContentLink>(cancellationToken) : null;
    }

    /// <summary>
    /// Reads the video's detail for the pane: a save is measured against THIS read (ADR 0794), so a change somebody else made
    /// since it was shown is a 412, not a lost update. Carries the document's rels, so nothing is read twice (ADR 0557).
    /// </summary>
    public async Task<TubeVideoForm> OpenAsync(TubeVideoRow video, CancellationToken cancellationToken = default)
    {
        var document = await DocumentAsync(video.Href("document"), cancellationToken);
        var detailHref = ApiCore.RelHref(document, "detail")
            ?? throw new ApiActionException($"This video does not offer 'detail'.");
        var (detail, etag) = await documents.GetDetailAsync(detailHref, cancellationToken);
        return new TubeVideoForm(video.Id, detailHref, detail, etag, ApiCore.ParseLinks(document));
    }

    /// <summary>One save, one request, measured against what the form was read with.</summary>
    public async Task SaveAsync(TubeVideoForm form, TubeVideoChange change, CancellationToken cancellationToken = default)
    {
        var fields = await VideoFieldsAsync(form.Baseline, cancellationToken);
        await documents.SaveDetailAsync(
            form.DetailHref,
            confirm =>
            {
                var body = TubeShelf.DetailBody(form.Baseline, fields, change, DateTimeOffset.Now);
                body["confirmDuplicateClaims"] = confirm;
                return body;
            },
            form.Etag,
            cancellationToken: cancellationToken);
    }

    /// <summary>Places a reference to the video in a channel; the core checks the channel's admission and the person's rights.</summary>
    public async Task PlaceAsync(TubeChannelRow channel, Guid videoId, CancellationToken cancellationToken = default) =>
        await references.CreateReferenceAsync(await ReferencesHrefAsync(channel, cancellationToken), videoId, cancellationToken);

    /// <summary>Takes a reference out of a channel. The video itself stays where it was prepared.</summary>
    public async Task RemoveFromChannelAsync(TubeChannelRow channel, Guid videoId, CancellationToken cancellationToken = default)
    {
        var placed = await references.GetReferencesAsync(await ReferencesHrefAsync(channel, cancellationToken), cancellationToken);
        var reference = placed.FirstOrDefault(r => r.TargetId == videoId)?.DeleteHref
            ?? throw new ApiActionException("This video can't be removed from this channel.");
        await references.DeleteReferenceAsync(reference, cancellationToken);
    }

    private async Task<string> ReferencesHrefAsync(TubeChannelRow channel, CancellationToken cancellationToken) =>
        ApiCore.RelHref(await DocumentAsync(channel.Href("document"), cancellationToken), "references")
        ?? throw new ApiActionException("This channel does not offer 'references'.");

    // The row names its document's address, so one read of it answers every rel the action follows (ADR 0557).
    private async Task<JsonElement> DocumentAsync(string? href, CancellationToken cancellationToken)
    {
        using var response = await core.Http.GetAsync(href ?? throw new ApiActionException("This row does not offer 'document'."), cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "Could not read the document", cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    // Structurally fixed, so asked once a session (ADR 0557); the mask is the one the video wears.
    private async Task<IReadOnlyDictionary<string, Guid>> VideoFieldsAsync(JsonElement detail, CancellationToken cancellationToken)
    {
        if (_videoFields is null)
        {
            var maskId = detail.GetProperty("maskId").GetGuid();
            var mask = (await masks.GetMasksAsync(cancellationToken)).FirstOrDefault(m => m.Id == maskId)
                ?? throw new ApiActionException("This video's mask can't be read.");
            _videoFields = (await masks.GetMaskFieldsAsync(mask, cancellationToken)).ToDictionary(f => f.Name, f => f.Id, StringComparer.Ordinal);
        }

        return _videoFields;
    }
}

/// <summary>A video's detail as read for the pane, with the tag a save of it is measured against.</summary>
public sealed record TubeVideoForm(Guid VideoId, string DetailHref, JsonElement Baseline, string? Etag, LinkMap? Links);
