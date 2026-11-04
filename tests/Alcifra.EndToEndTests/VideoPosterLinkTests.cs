using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

// #2160, ADR 1053: a video's version names its picture through the poster rel, and only where a reference field named
// Thumbnail says which document that is. The rel names the picture's own versions, so its rights answer for it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class VideoPosterLinkTests
{
    private readonly E2EApiFactory _factory;

    public VideoPosterLinkTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_video_with_a_thumbnail_field_names_its_picture_and_other_versions_do_not()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"poster-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "poster-1234", "Poster User", canManageRepositories: true);
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "poster-1234"));

        var mask = await TestJson.Post(api, "/api/masks", new
        {
            name = $"Clip {Guid.NewGuid():N}",
            fields = new object[] { new { name = "Thumbnail", dataType = (int)FieldDataType.DocumentReference, isRequired = false, isList = false } },
        });
        var fieldId = mask.GetProperty("fields").EnumerateArray().Single().GetProperty("id").GetGuid();
        var repo = (await TestJson.Post(api, "/api/repositories", new { name = $"Clips {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        async Task<Guid> ChildAsync(string name) =>
            (await TestJson.Post(api, $"/api/documents/{repo}/children", new { name })).GetProperty("id").GetGuid();

        var picture = await ChildAsync($"picture-{Guid.NewGuid():N}");
        var named = await ChildAsync($"named-{Guid.NewGuid():N}");
        var plain = await ChildAsync($"plain-{Guid.NewGuid():N}");
        var text = await ChildAsync($"text-{Guid.NewGuid():N}");

        foreach (var document in new[] { named, text })
        {
            await TestJson.Put(api, $"/api/documents/{document}/mask", new { maskId = mask.GetProperty("id").GetGuid() });
            await TestJson.Put(api, $"/api/documents/{document}/index-data", new
            {
                fields = new object[] { new { fieldDefinitionId = fieldId, values = new[] { picture.ToString() } } },
            });
        }

        Assert.Equal($"/api/documents/{picture}/versions", PosterOf(await VersionAsync(api, named, ".mp4")));
        Assert.Null(PosterOf(await VersionAsync(api, plain, ".mp4")));
        Assert.Null(PosterOf(await VersionAsync(api, text, ".txt")));
    }

    private static async Task<JsonElement> VersionAsync(HttpClient api, Guid document, string extension)
    {
        var created = await TestJson.Post(api, $"/api/documents/{document}/versions", new { fileExtension = extension });
        var id = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Guid.NewGuid().ToByteArray())))
                .EnsureSuccessStatusCode();
        }

        (await api.PutAsJsonAsync($"/api/documents/{document}/versions/{id}", new { })).EnsureSuccessStatusCode();
        return await TestJson.Get(api, $"/api/documents/{document}/versions/{id}");
    }

    private static string? PosterOf(JsonElement version) =>
        version.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == "poster")
            .Select(l => l.GetProperty("href").GetString())
            .SingleOrDefault();
}
