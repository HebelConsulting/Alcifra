using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Provisioning;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The Demo tenant's unattended SETUP principal (ADR 0949): what the kiosk's messenger-seed.sh acts as. Narrow on
// purpose — it manages modules and repositories, holds no grant, and owns only what it creates.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class DemoSetupAccountTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_setup_account_creates_and_owns_a_repository_and_does_nothing_else()
    {
        var tenantId = await factory.SeedTenantNamedAsync($"demo-setup-{Guid.NewGuid():N}");
        var clientId = $"demo-setup-{Guid.NewGuid():N}";
        const string secret = "an-e2e-only-demo-setup-secret-of-sufficient-length";

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Setup:ServiceAccount:ClientId"] = clientId,
                ["Demo:Setup:ServiceAccount:ClientSecret"] = secret,
                ["Demo:Setup:ServiceAccount:Name"] = "demo-setup",
            }).Build();

            // Exactly as DemoDataSeeder calls it: two rights, no grant.
            Assert.True(await SeededServiceAccount.AddIfConfiguredAsync(scope.ServiceProvider, db, configuration, "Demo:Setup", tenantId,
                grantOn: null, rights: account => (account.CanManageModules, account.CanManageRepositories) = (true, true), grant: null));
            await db.SaveChangesAsync();
            var account = await db.ServiceAccounts.IgnoreQueryFilters().SingleAsync(a => a.OpenIddictApplicationClientId == clientId);
            Assert.Empty(await db.AclEntries.IgnoreQueryFilters().Where(e => e.ServiceAccountId == account.Id).ToListAsync());
        }

        using var api = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));

        Assert.Empty((await TestJson.Get(api, "/api/repositories")).GetProperty("repositories").EnumerateArray());   // no grant: sees nothing
        using var created = await api.PostAsJsonAsync("/api/repositories", new { name = $"Messages {Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var repository = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var mine = await TestJson.Get(api, $"/api/documents/{repository.GetProperty("id").GetGuid()}");
        Assert.True(mine.GetProperty("canManagePermissions").GetBoolean());   // its creator: it may grant the module there

        using (var settings = await api.GetAsync("/api/tenant-settings"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        }

        using (var accounts = await api.GetAsync("/api/service-accounts"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, accounts.StatusCode);
        }
    }
}
