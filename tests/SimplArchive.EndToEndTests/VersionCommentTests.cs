using System.Text;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API for ADR 0528: a version can carry a "why this revision" comment, set at creation
// and read back from the versions list.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class VersionCommentTests
{
    private readonly E2EApiFactory _factory;

    public VersionCommentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Version_comment_round_trips_through_create_and_list()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"VC {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "Report" })).GetProperty("id").GetGuid();

        // Create a version WITH a comment.
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt", comment = "corrected the Q3 totals" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("v1")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        // The comment comes back on the version in the list.
        var version = (await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray()
            .Single(v => v.GetProperty("id").GetGuid() == versionId);
        Assert.Equal("corrected the Q3 totals", version.GetProperty("comment").GetString());
    }

    // ADR 0974: a version names its creator by USER ID as well as by name — the details pane's "Message" acts on that
    // person. A service account is not a person, so it has no id to act on.
    [Fact]
    public async Task A_version_names_a_person_creator_by_user_id_and_a_service_account_by_none()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // EACH CALLER FILES IN ITS OWN SPACE, so nobody has to be granted anything: the version resource's creator is
        // what this test is about, and an ACL write would make it depend on the rights machinery instead (ADR 0884 — a
        // test that mutates creates its own). It also used to fail 403 in CI for exactly that reason: the person was
        // granted content-edit on the service account's document, and whether that grant reached the effective rights
        // was a second subject smuggled into this one.
        var email = $"creator-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "creator1234", "Creator", canManageRepositories: true);
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "creator1234"));
        var userId = (await TestJson.Get(user, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();

        var userRepo = (await TestJson.Post(user, "/api/repositories", new { name = $"CU {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var userDoc = (await TestJson.Post(user, $"/api/documents/{userRepo}/children", new { name = "Doc" })).GetProperty("id").GetGuid();
        var byUser = await UploadAsync(user, userDoc);

        var serviceRepo = (await TestJson.Post(owner, "/api/repositories", new { name = $"CS {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var serviceDoc = (await TestJson.Post(owner, $"/api/documents/{serviceRepo}/children", new { name = "Doc" })).GetProperty("id").GetGuid();
        var byService = await UploadAsync(owner, serviceDoc);

        var userVersions = (await TestJson.Get(user, $"/api/documents/{userDoc}/versions")).GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(userId,
            userVersions.Single(v => v.GetProperty("id").GetGuid() == byUser).GetProperty("createdByUserId").GetGuid());

        var serviceVersions = (await TestJson.Get(owner, $"/api/documents/{serviceDoc}/versions")).GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null,
            serviceVersions.Single(v => v.GetProperty("id").GetGuid() == byService).GetProperty("createdByUserId").ValueKind);
    }

    // A version filed with content and finalized, as its caller.
    private static async Task<Guid> UploadAsync(HttpClient client, Guid documentId)
    {
        var created = await TestJson.Post(client, $"/api/documents/{documentId}/versions", new { fileExtension = ".txt" });
        var id = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("v")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(client, $"/api/documents/{documentId}/versions/{id}", new { });
        return id;
    }
}
