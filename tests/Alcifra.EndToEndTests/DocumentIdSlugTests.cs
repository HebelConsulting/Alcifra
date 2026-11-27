using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// A create may choose the identity it asks for (#2501): a caller's `idSlug` derives the document's id from the tenant
// (CallerDocumentIds), so the kiosk's tube seed keeps its channels, playlists, videos and thumbnails across the
// nightly reset without a lookup by name. Created answers 201; a slug whose id is already held answers THAT document
// with 200 — never a duplicate, never an overwrite.
//
// The two ways this could rot silently: the second create making a same-named duplicate under a new id, and a slug
// landing on someone else's document. Both are asserted here.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentIdSlugTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_create_with_an_idSlug_is_idempotent_by_that_identity()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"slug-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, email, "u-1234", "Slug Owner", canManageRepositories: true);
        using var owner = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, "u-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Slug {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var slug = $"folder/{Guid.NewGuid():N}";
        var expected = Alcifra.Api.Documents.CallerDocumentIds.For(tenantId, slug);

        var first = await owner.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = "Contracts", idSlug = slug });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(expected, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        // The same slug again: 200 with the SAME document — one folder named Contracts, not two.
        var second = await owner.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = "Contracts", idSlug = slug });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(expected, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        var children = await TestJson.Get(owner, $"/api/documents/{repoId}/children");
        Assert.Single(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "Contracts");

        // A blank slug is the caller's mistake, not a document.
        var blank = await owner.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = "Blank", idSlug = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }
}
