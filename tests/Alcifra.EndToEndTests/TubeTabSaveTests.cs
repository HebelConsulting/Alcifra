using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Presentation;

namespace Alcifra.EndToEndTests;

// The Tube tab's save against the real core (#2148): the body it builds from a read detail is accepted, changes what it
// names, keeps the rest, and is measured against the read that filled the pane.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TubeTabSaveTests(E2EApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private async Task<(HttpClient Owner, string Detail, IReadOnlyDictionary<string, Guid> Fields)> VideoAsync()
    {
        var (clientId, secret, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var (maskId, fields) = await factory.SeedMaskWithTextFieldsAsync(tenantId, TubeShelf.TagsField, TubeShelf.PublishedField, TubeShelf.PublishedAtField, TubeShelf.DescriptionField, "Language");

        var repo = (await TestJson.Post(owner, "/api/repositories", new { name = $"Tube {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var id = (await TestJson.Post(owner, $"/api/documents/{repo}/children", new { name = $"clip-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var detail = $"/api/documents/{id}/detail";

        var (read, tag) = await ReadAsync(owner, detail);
        using var assign = await PutAsync(owner, detail, tag, new
        {
            name = read.GetProperty("name").GetString(),
            maskId,
            tags = new[] { "kept" },
            ocrLanguages = Array.Empty<string>(),
            fields = new[] { new { fieldDefinitionId = fields["Language"], values = new[] { "de" } } },
        });
        assign.EnsureSuccessStatusCode();
        return (owner, detail, fields);
    }

    private static async Task<(JsonElement Detail, string Tag)> ReadAsync(HttpClient api, string href)
    {
        using var response = await api.GetAsync(href);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(), response.Headers.ETag!.Tag);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient api, string href, string tag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return api.SendAsync(request);
    }

    private static string[] Values(JsonElement detail, Guid field) =>
        [.. detail.GetProperty("fields").EnumerateArray()
            .Where(f => f.GetProperty("fieldDefinitionId").GetGuid() == field)
            .SelectMany(f => f.GetProperty("values").EnumerateArray().Select(v => v.GetString()!))];

    [Fact]
    public async Task Publishing_and_editing_change_what_they_name_and_keep_the_rest()
    {
        var (owner, href, fields) = await VideoAsync();
        using var _ = owner;

        var (read, tag) = await ReadAsync(owner, href);
        using (var publish = await PutAsync(owner, href, tag, TubeShelf.DetailBody(read, fields, new TubeVideoChange(Published: true), Now)))
        {
            publish.EnsureSuccessStatusCode();
        }

        var (published, tag2) = await ReadAsync(owner, href);
        Assert.Equal(["true"], Values(published, fields[TubeShelf.PublishedField]));
        Assert.Single(Values(published, fields[TubeShelf.PublishedAtField]));
        Assert.Equal(["de"], Values(published, fields["Language"]));
        Assert.Equal(["kept"], published.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());

        var rename = new TubeVideoChange(Name: "Renamed clip", Description: "Line one", Tags: ["a", "b"]);
        using (var edit = await PutAsync(owner, href, tag2, TubeShelf.DetailBody(published, fields, rename, Now)))
        {
            edit.EnsureSuccessStatusCode();
        }

        var (edited, _) = await ReadAsync(owner, href);
        Assert.Equal("Renamed clip", edited.GetProperty("name").GetString());
        Assert.Equal(["Line one"], Values(edited, fields[TubeShelf.DescriptionField]));
        Assert.Equal(["a", "b"], Values(edited, fields[TubeShelf.TagsField]));
        Assert.Equal(["true"], Values(edited, fields[TubeShelf.PublishedField]));
    }

    [Fact]
    public async Task A_save_is_measured_against_the_read_that_filled_the_pane()
    {
        var (owner, href, fields) = await VideoAsync();
        using var _ = owner;

        var (read, tag) = await ReadAsync(owner, href);
        using (var first = await PutAsync(owner, href, tag, TubeShelf.DetailBody(read, fields, new TubeVideoChange(Published: true), Now)))
        {
            first.EnsureSuccessStatusCode();
        }

        using var stale = await PutAsync(owner, href, tag, TubeShelf.DetailBody(read, fields, new TubeVideoChange(Description: "Too late"), Now));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }
}
