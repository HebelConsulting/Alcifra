using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Client.Models;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>
/// What a reference field's picker needs from the server (#2214, ADR 1068): its candidates with their pictures, the
/// current target's picture, and filing a picked file as the one accepted mask. The desktop's ReferencePickerWiring is its
/// twin. Every address is followed from a rel the server advertised, never composed (ADR 0543).
/// </summary>
public sealed class ReferencePicker(HttpClient http)
{
    /// <summary>The documents the field may point at for what was typed, each with its picture's address where it has one.</summary>
    public async Task<IEnumerable<ReferenceChoice>> CandidatesAsync(string targetsHref, string? typed, CancellationToken cancellationToken = default)
    {
        var href = typed is { Length: > 0 } ? $"{targetsHref}{(targetsHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed)}" : targetsHref;
        JsonElement answer;
        try
        {
            answer = await http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }

        var choices = answer.TryGetProperty("targets", out var targets)
            ? targets.EnumerateArray().Select(t => new ReferenceChoice
            {
                Id = t.GetProperty("id").GetGuid(),
                Name = t.GetProperty("name").GetString() ?? string.Empty,
                VersionsHref = Rel(t, "versions"),
            }).ToList()
            : [];
        await Task.WhenAll(choices.Select(c => WithPictureAsync(c, cancellationToken)));
        return choices;
    }

    /// <summary>Reads <paramref name="choice"/>'s picture address: its current version's preview. Best-effort.</summary>
    public async Task WithPictureAsync(ReferenceChoice choice, CancellationToken cancellationToken = default)
    {
        try
        {
            var versionsHref = choice.VersionsHref
                ?? (choice.DocumentHref is { } documentHref ? Rel(await http.GetFromJsonAsync<JsonElement>(documentHref, cancellationToken), "versions") : null);
            if (versionsHref is null)
            {
                return;
            }

            // The current version as every pane picks it; an enveloped one has no picture an image element can load.
            var versions = await http.GetFromJsonAsync<VersionListResponse>(versionsHref, cancellationToken);
            var current = VersionListResponse.PickCurrent(versions?.Versions?.Where(v => v.Status == "Confirmed").ToList() ?? [], versions?.CurrentVersionId);
            choice.PictureUrl = current is { ContentIsEnveloped: false } ? http.Absolute(Hypermedia.Links.Href(current.Links, "preview")) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            choice.PictureUrl = null;
        }
    }

    /// <summary>
    /// Files <paramref name="bytes"/> into <paramref name="childrenHref"/> and gives it <paramref name="maskId"/>: create,
    /// version, the bytes to storage, finalize, then the document's own mask rel, since a create names only a folder's mask.
    /// Null means the name is taken there.
    /// </summary>
    public async Task<ReferenceChoice?> FileAsync(string childrenHref, string fileName, byte[] bytes, string? contentType, Guid maskId)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        using var create = await http.PostAsJsonAsync(childrenHref, new { name });
        if (create.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        using var versionResponse = await http.PostAsJsonAsync(Required(created, "versions"), new { fileExtension = Path.GetExtension(fileName) });
        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>();
        if (version.TryGetProperty("encryption", out var encryption) && encryption.ValueKind == JsonValueKind.Object)
        {
            // The browser does not seal before the upload (the drop zone's script does that); refused rather than plaintext.
            throw new InvalidOperationException(Strings.Get("RefPickerEncrypts"));
        }

        using var content = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        (await PresignedTransfer.Anonymous.PutAsync(version.GetProperty("uploadUrl").GetString()!, content)).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync(Required(version, "self"), new { })).EnsureSuccessStatusCode();

        var filed = await http.GetFromJsonAsync<JsonElement>(Required(created, "self"));
        (await http.PutAsJsonAsync(Required(filed, "mask"), new { maskId })).EnsureSuccessStatusCode();
        var choice = new ReferenceChoice { Id = created.GetProperty("id").GetGuid(), Name = name, VersionsHref = Rel(filed, "versions") };
        await WithPictureAsync(choice);
        return choice;
    }

    /// <summary>The address the folder a document sits in files into: its "parent" rel, then the parent's "children" rel.</summary>
    public async Task<string?> FolderChildrenAsync(string? parentHref) =>
        parentHref is null ? null : Rel(await http.GetFromJsonAsync<JsonElement>(parentHref), "children");

    private static string? Rel(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
                ? link.GetProperty("href").GetString()
                : null
            : null;

    private static string Required(JsonElement resource, string rel) =>
        Rel(resource, rel) ?? throw new InvalidOperationException($"The resource advertised no '{rel}' rel (ADR 0543).");
}
