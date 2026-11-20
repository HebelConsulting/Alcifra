using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alcifra.EndToEndTests;

// A grant is READ only at the document's governing scope (ADR 0183): the document itself when it breaks inheritance,
// else the nearest ancestor that does. An entry written below it answered 2xx and granted nothing — a row nothing
// looks at, which the person who wrote it read as "access given" (#1921). The ACL endpoint now refuses that write,
// naming the scope to grant on instead (ADR 1096), and a root — its own scope, whatever its flag says — stays exempt.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class AclGrantScopeTests
{
    private const string GranteePassword = "aclscope1234";

    private readonly E2EApiFactory _factory;

    public AclGrantScopeTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Admin, Guid TenantId, Guid RepositoryId, Guid FolderId, Guid GranteeId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var creator = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(creator, "/api/repositories", new { name = $"Acl scope {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(creator, $"/api/documents/{repoId}/children", new { name = "Workshop" }))
            .GetProperty("id").GetGuid();

        var adminEmail = $"acl-scope-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, GranteePassword, "Acl Scope Admin", isTenantAdmin: true);
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, GranteePassword));

        var granteeEmail = $"acl-scope-{Guid.NewGuid():N}@e2e.local";
        var granteeId = await _factory.SeedUserAsync(tenantId, granteeEmail, GranteePassword, "Grantee");
        return new Rig(admin, tenantId, repoId, folderId, granteeId);
    }

    private static string GrantUrl(Guid documentId, Guid userId) => $"/api/documents/{documentId}/acl-entries/users/{userId}";

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("errorCode").GetString()!;

    [Fact]
    public async Task A_grant_on_an_inheriting_document_is_refused_naming_the_governing_scope()
    {
        var rig = await RigAsync();

        using var refused = await rig.Admin.PutAsJsonAsync(GrantUrl(rig.FolderId, rig.GranteeId), new { canSee = true, canReadContent = true });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("ACL_GRANT_OUTSIDE_GOVERNING_SCOPE", await ErrorCodeAsync(refused));
        // The sentence says where the entry WOULD be read — the repository is the governing scope — so a person has
        // the remedy in hand rather than a dead end.
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("grant on", body, StringComparison.OrdinalIgnoreCase);

        // Nothing was written: the folder's own entries are still empty.
        var listing = await TestJson.Get(rig.Admin, $"/api/documents/{rig.FolderId}/acl-entries");
        Assert.Empty(listing.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task The_same_grant_on_the_governing_scope_succeeds()
    {
        var rig = await RigAsync();

        using var granted = await rig.Admin.PutAsJsonAsync(GrantUrl(rig.RepositoryId, rig.GranteeId), new { canSee = true, canReadContent = true });

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var listing = await TestJson.Get(rig.Admin, $"/api/documents/{rig.RepositoryId}/acl-entries");
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), e => e.GetProperty("principalId").GetGuid() == rig.GranteeId);
    }

    [Fact]
    public async Task Breaking_inheritance_first_makes_the_grant_legal_and_effective()
    {
        var rig = await RigAsync();

        (await rig.Admin.PutAsJsonAsync($"/api/documents/{rig.FolderId}/acl-entries/inheritance", new { breaksInheritance = true }))
            .EnsureSuccessStatusCode();
        using (var granted = await rig.Admin.PutAsJsonAsync(GrantUrl(rig.FolderId, rig.GranteeId), new { canSee = true, canReadContent = true }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        // The grant is READ now — the acceptance that used to be a lie, proven on the grantee's own request.
        var grantee = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(await EmailOfAsync(rig), GranteePassword));
        Assert.Equal(HttpStatusCode.OK, (await grantee.GetAsync($"/api/documents/{rig.FolderId}")).StatusCode);
    }

    [Fact]
    public async Task A_repository_root_takes_grants_whatever_its_own_inheritance_flag_says()
    {
        // A root is the ultimate fallback of the scope walk (ADR 0183): its entries are read even though it has no
        // parent to break away from and its flag stays false.
        var rig = await RigAsync();

        using var granted = await rig.Admin.PutAsJsonAsync(GrantUrl(rig.RepositoryId, rig.GranteeId), new { canSee = true });

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
    }

    private async Task<string> EmailOfAsync(Rig rig)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = rig.TenantId;
        return await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Users
            .Where(u => u.Id == rig.GranteeId).Select(u => u.Email).SingleAsync();
    }
}
