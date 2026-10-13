using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// "Changes since" (ADR 0925; SimplArchiveMobile #5): an offline copy asks what changed after its cursor. The caller
// sees only rows it may see (the listing's rule, ADR 0765), a deletion comes back as deleted, the cursor lags "now"
// by the settle window, and a cursor the endpoint did not issue is refused rather than treated as "start now".
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ChangesEndpointTests
{
    private static readonly TimeSpan PastTheSettleWindow = TimeSpan.FromSeconds(6);
    private readonly E2EApiFactory _factory;

    public ChangesEndpointTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_viewer_learns_what_it_may_see_changed_and_what_was_deleted()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await PostJson(owner, "/api/repositories", new { name = $"Changes {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var visibleId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "visible" })).GetProperty("id").GetGuid();
        var hiddenId = (await PostJson(owner, $"/api/documents/{repoId}/children", new { name = "hidden" })).GetProperty("id").GetGuid();
        await PutJson(owner, $"/api/documents/{hiddenId}/acl-entries/inheritance", new { breaksInheritance = true });
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
        var viewerId = await _factory.SeedUserAsync(tenantId, viewerEmail, "ViewerPw123!", "Viewer");
        await PutJson(owner, $"/api/documents/{repoId}/acl-entries/users/{viewerId}", new { canSee = true, canReadContent = true });
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "ViewerPw123!"));

        // Followed from the root, as a client must (ADR 0543).
        var changesHref = (await GetJson(viewer, "/api")).GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "changes").GetProperty("href").GetString()!;
        var start = await GetJson(viewer, changesHref);
        Assert.Empty(start.GetProperty("changes").EnumerateArray());   // no cursor: no history, a starting point
        var cursor = start.GetProperty("cursor").GetString()!;

        await Task.Delay(PastTheSettleWindow);   // the starting cursor sits a window behind "now"
        await RenameAsync(owner, visibleId, "visible renamed");
        await RenameAsync(owner, hiddenId, "hidden renamed");
        await Task.Delay(PastTheSettleWindow);

        var changed = await GetJson(viewer, $"{changesHref}?since={Uri.EscapeDataString(cursor)}");
        var rows = changed.GetProperty("changes").EnumerateArray().ToList();
        var visible = Assert.Single(rows, r => r.GetProperty("id").GetGuid() == visibleId);
        Assert.Equal("visible renamed", visible.GetProperty("name").GetString());
        Assert.False(visible.GetProperty("deleted").GetBoolean());
        Assert.Equal(repoId, visible.GetProperty("parentId").GetGuid());
        Assert.DoesNotContain(rows, r => r.GetProperty("id").GetGuid() == hiddenId);   // never named to a caller who cannot see it

        cursor = changed.GetProperty("cursor").GetString()!;
        var toDelete = await owner.GetAsync($"/api/documents/{visibleId}");
        toDelete.EnsureSuccessStatusCode();
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{visibleId}"))
        {
            delete.Headers.TryAddWithoutValidation("If-Match", toDelete.Headers.ETag!.Tag);
            (await owner.SendAsync(delete)).EnsureSuccessStatusCode();
        }
        await Task.Delay(PastTheSettleWindow);

        var afterDelete = (await GetJson(viewer, $"{changesHref}?since={Uri.EscapeDataString(cursor)}")).GetProperty("changes").EnumerateArray().ToList();
        Assert.True(Assert.Single(afterDelete, r => r.GetProperty("id").GetGuid() == visibleId).GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task A_cursor_the_endpoint_did_not_issue_is_refused()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var client = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var response = await client.GetAsync("/api/changes?since=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("INVALID_CHANGES_CURSOR", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_answers_without_a_body()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var client = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/changes"));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task RenameAsync(HttpClient client, Guid id, string name)
    {
        var current = await client.GetAsync($"/api/documents/{id}");
        current.EnsureSuccessStatusCode();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = JsonContent.Create(new { name }) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        (await client.SendAsync(put)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task PutJson(HttpClient client, string url, object body) =>
        (await client.PutAsJsonAsync(url, body)).EnsureSuccessStatusCode();

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
