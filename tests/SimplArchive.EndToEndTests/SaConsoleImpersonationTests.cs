
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SimplArchive.Api.Provisioning;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.EndToEndTests;

// `alcifra messenger … --as` (core #1813, ADR 0947): the CLI's own impersonation path against a real server — the
// root's `users`, the row's `impersonate` rel, and the token exchange with the alcifra client, whose registration
// now carries the grant. What the token then does is ordinary API work, already covered elsewhere.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SaConsoleImpersonationTests(E2EApiFactory factory)
{
    [Fact]
    public async Task An_administrator_allowed_to_impersonate_acts_as_the_user_through_saconsoles_client()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var actorEmail = $"mover-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, actorEmail, "mover-1234", "Mover", canManageUsers: true);
        await factory.GrantCanImpersonateAsync(actorEmail);
        var targetEmail = $"moved-{Guid.NewGuid():N}@e2e.local";
        var targetId = await factory.SeedUserAsync(tenantId, targetEmail, "moved-1234", "Moved User");
        var actorToken = await factory.GetUserTokenAsync(actorEmail, "mover-1234");
        using var http = factory.CreateAuthedClient(actorToken);

        var token = await Impersonation.TokenForAsync(http, actorToken, targetEmail.ToUpperInvariant(), CancellationToken.None);

        using var asTarget = factory.CreateAuthedClient(token);
        var whoami = await TestJson.Get(asTarget, "/api/diagnostics/whoami");
        Assert.Equal(targetId, whoami.GetProperty("userId").GetGuid());
        Assert.Equal("Mover", whoami.GetProperty("impersonatedBy").GetString());   // every act recorded as on behalf
    }

    [Fact]
    public async Task Without_the_right_the_row_offers_no_impersonation_and_the_tool_says_so()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var actorEmail = $"plain-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, actorEmail, "plain-1234", "Plain", canManageUsers: true);
        var targetEmail = $"other-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, targetEmail, "other-1234", "Other");
        var actorToken = await factory.GetUserTokenAsync(actorEmail, "plain-1234");
        using var http = factory.CreateAuthedClient(actorToken);

        var refused = await Assert.ThrowsAsync<CliException>(() => Impersonation.TokenForAsync(http, actorToken, targetEmail, CancellationToken.None));
        Assert.Contains("impersonate", refused.Message, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<CliException>(() => Impersonation.TokenForAsync(http, actorToken, "nobody@e2e.local", CancellationToken.None));
        Assert.Contains("no user nobody@e2e.local", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installation_seeded_before_the_grant_is_healed_at_start()
    {
        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await applications.FindByClientIdAsync(SaConsoleClientSeeder.ClientId) ?? throw new InvalidOperationException("no saconsole client");
        var stale = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(stale, client);
        stale.Permissions.Remove(SaConsoleClientSeeder.TokenExchange);   // as an installation seeded before it
        await applications.UpdateAsync(client, stale);

        await SaConsoleClientSeeder.SeedAsync(applications);

        var healed = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(healed, (await applications.FindByClientIdAsync(SaConsoleClientSeeder.ClientId))!);
        Assert.Contains(SaConsoleClientSeeder.TokenExchange, healed.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.DeviceCode, healed.Permissions);   // nothing else lost
    }
}
