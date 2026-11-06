using System.Net;
using System.Text;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// The administrator's way into every personal space (ADR 1066), over the real API + Postgres + object storage: the
// Administration folder is a repository root the administrator's tree lists, Users beneath it references every
// personal space, each row carries what browsing the space needs, listing it is recorded, and anyone else sees none
// of it. It replaces the tests of the retired `GET /api/admin/personal-repositories` (ADR 0377), which asserted the
// same four things of that listing.
//
// What grants the browse is CanAccessWithoutGrant (ADR 0670), NOT the IsTenantAdmin bypass, which stops at the edge of
// somebody else's personal space. The second test is the one that says so: same admin, same tenant, right revoked,
// and the rows go quiet rather than half-working.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AdministrationFolderTests
{
    private readonly E2EApiFactory _factory;

    public AdministrationFolderTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_reaches_every_space_through_the_Users_folder_it_is_audited_and_others_see_nothing()
    {
        var (tenantId, usersId) = await TenantWithAdministrationAsync();

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin", canViewAuditLog: true);
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        // Two ordinary users, each with a personal repo holding a private document — made AFTER the Users folder, so
        // their rows are the ones the provisioner placed, not ones a start reconciled.
        var aliceEmail = $"alice-{Guid.NewGuid():N}@e2e.local";
        var bobEmail = $"bob-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, aliceEmail, "u-1234", "Alice");
        await _factory.SeedUserAsync(tenantId, bobEmail, "u-1234", "Bob");
        using var alice = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aliceEmail, "u-1234"));
        using var bob = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bobEmail, "u-1234"));

        var aliceRepo = (await TestJson.Post(alice, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var bobRepo = (await TestJson.Post(bob, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var aliceDoc = await UploadAsync(alice, await MyDocumentsAsync(alice, aliceRepo), "alice-secret");

        // Administration is a root in the admin's tree, found by what it wears; Users is its one child; the rows under
        // Users are the spaces. Each hop FOLLOWED from the row before it (ADR 0543).
        var administration = (await TestJson.Get(admin, "/api/repositories")).GetProperty("repositories").EnumerateArray()
            .Single(r => r.TryGetProperty("icon", out var icon) && icon.GetString() == "administration");
        var users = (await TestJson.Get(admin, Href(administration, "children"))).GetProperty("children").EnumerateArray()
            .Single();
        Assert.Equal(usersId, users.GetProperty("id").GetGuid());
        var rows = (await TestJson.Get(admin, Href(users, "references"))).GetProperty("references").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("id").GetGuid() == aliceRepo && r.GetProperty("name").GetString() == "Alice");
        Assert.Contains(rows, r => r.GetProperty("id").GetGuid() == bobRepo);

        // The admin browses into Alice's space through the row and sees her private document — which sits in My
        // Documents, since the first level holds only provisioned folders (#634). The bypass is what this asserts,
        // so it browses with the ADMIN's client all the way down.
        var aliceRow = rows.Single(r => r.GetProperty("id").GetGuid() == aliceRepo);
        var children = await TestJson.Get(admin, $"/api/documents/{await MyDocumentsAsync(admin, aliceRepo)}/children");
        Assert.Contains(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("id").GetGuid() == aliceDoc);

        // Opening a space lists its children AND its shortcuts, so the row carries both, plus the document itself —
        // and each answers (a rel that 404s is worse than an absent one).
        foreach (var rel in new[] { "children", "references", "self" })
        {
            Assert.True((await admin.GetAsync(Href(aliceRow, rel))).IsSuccessStatusCode, $"the '{rel}' rel the Users row advertised did not answer");
        }

        // The access was recorded — listing Users IS the listing of personal spaces (ADR 0377's decision, kept).
        var audit = await TestJson.Get(admin, "/api/audit-events?action=Admin.ViewedPersonalSpaces");
        Assert.Contains(audit.GetProperty("events").EnumerateArray(), e => e.GetProperty("action").GetString() == "Admin.ViewedPersonalSpaces");

        // Alice sees none of it: no Administration root in her tree (it carries no grant), and the Users folder is
        // refused outright rather than listed empty.
        Assert.DoesNotContain((await TestJson.Get(alice, "/api/repositories")).GetProperty("repositories").EnumerateArray(),
            r => r.TryGetProperty("icon", out var icon) && icon.GetString() == "administration");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/documents/{usersId}/references")).StatusCode);
    }

    [Fact]
    public async Task An_admin_who_revoked_their_own_access_sees_no_spaces_and_cannot_browse_one()
    {
        var (tenantId, usersId) = await TenantWithAdministrationAsync();

        var adminEmail = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "adm-1234", "Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "adm-1234"));

        var carolEmail = $"carol-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, carolEmail, "u-1234", "Carol");
        using var carol = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(carolEmail, "u-1234"));
        var carolRepo = (await TestJson.Post(carol, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var carolDoc = await UploadAsync(carol, await MyDocumentsAsync(carol, carolRepo), "carol-secret");

        // While the admin holds the right, the row is there — the control, so the assertions below cannot pass for
        // the trivial reason that the fixture never granted anything.
        Assert.Contains(await UsersRowsAsync(admin, usersId), r => r.GetProperty("id").GetGuid() == carolRepo);

        await _factory.RevokeAccessWithoutGrantAsync(adminEmail);

        // The folder is still theirs to open — it is the TARGETS they may no longer see, so the rows go (ADR 0765:
        // a reference whose target the caller cannot see is not listed)...
        Assert.DoesNotContain(await UsersRowsAsync(admin, usersId), r => r.GetProperty("id").GetGuid() == carolRepo);

        // ...and the space itself is genuinely closed, not merely unlisted. Still a tenant admin: this is the
        // narrowed bypass, so the SAME caller keeps full rights outside the personal space. 403 rather than 404 is
        // the API's existing answer for "no CanSee" everywhere.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/documents/{carolDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/repositories")).StatusCode);
    }

    // A tenant with its Administration folder, as provisioning makes one: the service account's repository stands in
    // for the standard repository whose author creates the folders.
    private async Task<(Guid TenantId, Guid UsersId)> TenantWithAdministrationAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Std{Guid.NewGuid():N}"[..12] })).GetProperty("id").GetGuid();
        return (tenantId, await _factory.EnsureAdministrationFoldersAsync(tenantId, repoId));
    }

    private static async Task<List<JsonElement>> UsersRowsAsync(HttpClient api, Guid usersId) =>
        (await TestJson.Get(api, $"/api/documents/{usersId}/references")).GetProperty("references").EnumerateArray().ToList();

    private static string Href(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private static async Task<Guid> UploadAsync(HttpClient client, Guid folderId, string name)
    {
        var docId = (await TestJson.Post(client, $"/api/documents/{folderId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(name)))).EnsureSuccessStatusCode();
        }
        await TestJson.Put(client, $"/api/documents/{docId}/versions/{versionId}", new { });
        return docId;
    }

    // The personal space's first level holds only the folders it was provisioned with (#634), so a test that
    // wants somewhere to put things asks for My Documents — which is where a user's own content goes.
    private static async Task<Guid> MyDocumentsAsync(HttpClient api, Guid personalId) =>
        (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents")
            .GetProperty("id").GetGuid();
}
