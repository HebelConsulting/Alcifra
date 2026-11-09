using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Domain.Groups;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// Copying a user gives the copy the very same system rights and groups (#2277). It used to be two requests, create then
// PUT the rights, and the second was refused for every tenant administrator: the caller's rights the escalation cap reads
// left out four rights, so nobody "held" them. The web ignored the refusal and the copy had no rights at all. Now a copy
// is one request: POST /api/users with copyFrom, all or nothing.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class UserCopyTests(E2EApiFactory factory)
{
    private const string Password = "copy-1234-Secret!";

    // The reported defect at its root: a tenant administrator holding every right may grant every right.
    [Fact]
    public async Task A_tenant_administrator_holding_every_right_may_grant_every_right()
    {
        var (admin, _, tenantId) = await AdminAsync();
        var target = await factory.SeedUserAsync(tenantId, $"target-{Guid.NewGuid():N}@e2e.local", Password, "Target");

        using var response = await admin.PutAsJsonAsync($"/api/users/{target}/rights", AllRights());

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_copy_carries_the_sources_rights_and_groups_in_one_request()
    {
        var (admin, adminId, tenantId) = await AdminAsync();
        var group = await GroupWithMemberAsync(tenantId, adminId);
        var email = $"copy-{Guid.NewGuid():N}@e2e.local";

        using var response = await admin.PostAsJsonAsync("/api/users", new { email, displayName = "The Copy", copyFrom = adminId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var source = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == adminId);
        var created = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == copyId);
        Assert.Equal(Alcifra.Api.Users.SystemRightsMapping.Describe(Alcifra.Api.Users.SystemRightsMapping.Read(source)),
            Alcifra.Api.Users.SystemRightsMapping.Describe(Alcifra.Api.Users.SystemRightsMapping.Read(created)));
        Assert.True(created.IsTenantAdmin);
        Assert.True(created.CanReleaseResources);
        Assert.True(await db.GroupMemberships.IgnoreQueryFilters().AnyAsync(m => m.GroupId == group && m.UserId == copyId));
    }

    // All or nothing: a caller who may manage users but holds fewer rights than the source is refused, and no user exists.
    [Fact]
    public async Task A_refused_copy_creates_nobody()
    {
        var (_, adminId, tenantId) = await AdminAsync();
        var managerEmail = $"manager-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, managerEmail, Password, "Manager", canManageUsers: true);
        using var manager = factory.CreateAuthedClient(await factory.GetUserTokenAsync(managerEmail, Password));
        var email = $"refused-{Guid.NewGuid():N}@e2e.local";

        using var response = await manager.PostAsJsonAsync("/api/users", new { email, displayName = "Refused", copyFrom = adminId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("INSUFFICIENT_RIGHTS_TO_GRANT", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var normalized = email.ToUpperInvariant();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized));
    }

    [Fact]
    public async Task A_copy_of_an_unknown_user_answers_404()
    {
        var (admin, _, _) = await AdminAsync();

        using var response = await admin.PostAsJsonAsync("/api/users",
            new { email = $"nobody-{Guid.NewGuid():N}@e2e.local", displayName = "Nobody", copyFrom = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("USER_TO_COPY_NOT_FOUND", await response.Content.ReadAsStringAsync());
    }

    // A tenant administrator holding every right on its own row, as the demo's administrator does.
    private async Task<(HttpClient Admin, Guid AdminId, Guid TenantId)> AdminAsync()
    {
        var tenantId = await factory.SeedTenantNamedAsync($"Copy {Guid.NewGuid():N}");
        var email = $"copy-admin-{Guid.NewGuid():N}@e2e.local";
        var adminId = await factory.SeedUserAsync(tenantId, email, Password, "Copy Admin");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == adminId);
            Alcifra.Api.Users.SystemRightsMapping.Apply(admin, AllRights());
            await db.SaveChangesAsync();
        }

        return (factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, Password)), adminId, tenantId);
    }

    private async Task<Guid> GroupWithMemberAsync(Guid tenantId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var group = new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = $"Copied group {Guid.NewGuid():N}" };
        db.Groups.Add(group);
        db.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();
        return group.Id;
    }

    private static Alcifra.Api.Controllers.SystemRights AllRights()
    {
        var rights = new Alcifra.Api.Controllers.SystemRights();
        foreach (var property in typeof(Alcifra.Api.Controllers.SystemRights).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            property.SetValue(rights, true);
        }

        return rights;
    }
}
