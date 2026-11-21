using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.EndToEndTests;

// What a deployment's reset-time seed needs from `alcifra` (ADR 1103's kiosk slice): a tenant provisioned with a
// password the seed ALREADY knows (so the showcase logins survive a nightly wipe), and a listing to find it again so
// a re-run provisions nothing twice. The API enforces the password policy on a supplied password like any other.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class TenantCommandTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_tenant_can_be_provisioned_with_a_password_the_seed_already_knows_and_found_again()
    {
        var (admin, api) = await PlatformAdminAsync();
        var name = $"Showcase {Guid.NewGuid():N}";
        var email = $"seed-{Guid.NewGuid():N}@e2e.local";
        const string password = "ShowcaseDemo2026!";

        var created = await TestJson.Post(admin, "/api/tenants", new
        {
            name,
            administratorEmail = email,
            administratorDisplayName = "Seed Admin",
            repositoryName = "Seed Repository",
            administratorPassword = password,
        });

        // The response states the password the caller supplied — the one thing a seed cannot re-read later.
        Assert.Equal(password, created.GetProperty("tenantAdministrator").GetProperty("password").GetString());

        // …and it actually signs in, which is the whole reason for supplying one.
        Assert.False(string.IsNullOrWhiteSpace(await factory.GetUserTokenAsync(email, password)));

        // The seed can find the tenant again by name, so a second run provisions nothing twice.
        var tenants = await TenantListSurface.ReadAsync(api, CancellationToken.None);
        Assert.Contains(tenants, row => row.Id == created.GetProperty("id").GetGuid() && row.Name == name);
    }

    [Fact]
    public async Task A_supplied_password_is_held_to_the_policy_like_any_other()
    {
        var (admin, _) = await PlatformAdminAsync();

        using var response = await admin.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Refused {Guid.NewGuid():N}",
            administratorEmail = $"refused-{Guid.NewGuid():N}@e2e.local",
            administratorDisplayName = "Refused Admin",
            repositoryName = "Refused Repository",
            administratorPassword = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("PASSWORD_TOO_SHORT", problem.GetProperty("errorCode").GetString());
    }

    private async Task<(HttpClient Admin, AlcifraApi Api)> PlatformAdminAsync()
    {
        var (clientId, secret) = await factory.SeedPlatformAdministratorAsync();
        var admin = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        return (admin, new AlcifraApi(admin));
    }
}
