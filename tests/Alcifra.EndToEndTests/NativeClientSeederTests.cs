using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Alcifra.Api.Provisioning;
using Alcifra.Api.Security;

namespace Alcifra.EndToEndTests;

// ADR 0916: the native apps' public clients are HEALED at startup, redirect URIs as well as permissions, so a change to
// either reaches an installation that already has the client. The desktop's seeder once healed permissions only.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class NativeClientSeederTests
{
    private readonly E2EApiFactory _factory;

    public NativeClientSeederTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_mobile_registration_that_lost_its_redirect_gets_it_back_and_keeps_an_operators_addition()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var existing = await applications.FindByClientIdAsync(MobileClientSeeder.ClientId);
        Assert.NotNull(existing);   // seeded at startup

        // An older registration: its redirect gone, one granted by hand instead.
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, existing);
        descriptor.RedirectUris.Clear();
        descriptor.RedirectUris.Add(new Uri("dev.simplarchive.mobile:/operator-added"));
        await applications.UpdateAsync(existing, descriptor);

        await MobileClientSeeder.SeedAsync(applications);

        var healed = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(healed, (await applications.FindByClientIdAsync(MobileClientSeeder.ClientId))!);
        Assert.Contains(new Uri(MobileRedirect.RedirectUri), healed.RedirectUris);
        Assert.Contains(new Uri("dev.simplarchive.mobile:/operator-added"), healed.RedirectUris);   // nothing removed
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, healed.Permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Prefixes.GrantType + Alcifra.Auth.ImpersonationConstants.TokenExchangeGrantType, healed.Permissions);   // impersonation stays a desktop act

        // Leave the registration as startup makes it, for the other tests in the collection.
        healed.RedirectUris.Remove(new Uri("dev.simplarchive.mobile:/operator-added"));
        await applications.UpdateAsync((await applications.FindByClientIdAsync(MobileClientSeeder.ClientId))!, healed);
    }

    // ADR 0981: the autofill extension's client is seeded at startup as a public client with no redirect URI — it is
    // never signed in interactively, only reached by the mobile app's token exchange.
    [Fact]
    public async Task The_autofill_client_is_seeded_public_without_a_redirect()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var autofill = await applications.FindByClientIdAsync(AutofillClientSeeder.ClientId);
        Assert.NotNull(autofill);

        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, autofill);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, descriptor.ClientType);
        Assert.Empty(descriptor.RedirectUris);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Endpoints.Authorization, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.GrantType + Alcifra.Auth.ImpersonationConstants.TokenExchangeGrantType, descriptor.Permissions);
    }

    // ADR 1064: an installation that still has a client from before the rename loses it at startup, with its sessions.
    // The current client beside it stays.
    [Fact]
    public async Task A_retired_client_is_removed_with_its_tokens_and_the_current_one_stays()
    {
        const string Retired = "simplarchive-desktop";
        string? tokenId;

        // Each step in a scope of its own, as startup finds these rows: nothing already tracked.
        await using (var seeding = _factory.Services.CreateAsyncScope())
        {
            var applications = seeding.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var tokens = seeding.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            Assert.Null(await applications.FindByClientIdAsync(Retired));   // startup already removed it

            var application = await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = Retired,
                ClientType = OpenIddictConstants.ClientTypes.Public,
            });
            tokenId = await tokens.GetIdAsync(await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                ApplicationId = await applications.GetIdAsync(application),
                Subject = Guid.NewGuid().ToString(),
                Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
                Status = OpenIddictConstants.Statuses.Valid,
            }));
        }

        await using (var removing = _factory.Services.CreateAsyncScope())
        {
            var applications = removing.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await PublicClientsSeed.SeedAsync(applications);   // the whole startup sequence, in one scope
            await RetiredClients.RemoveAsync(applications);    // nothing left to do is not a failure
        }

        await using var after = _factory.Services.CreateAsyncScope();
        var remaining = after.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        Assert.Null(await remaining.FindByClientIdAsync(Retired));
        Assert.Null(await after.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().FindByIdAsync(tokenId!));
        Assert.NotNull(await remaining.FindByClientIdAsync(DesktopClientSeeder.ClientId));
    }

    [Theory]
    [InlineData("simplarchive-desktop")]
    [InlineData("simplarchive-mobile")]
    [InlineData("simplarchive-mobile-autofill")]
    [InlineData("saconsole")]
    [InlineData("sapass")]
    public async Task No_client_from_before_the_rename_is_registered(string clientId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        Assert.Null(await applications.FindByClientIdAsync(clientId));
    }

    [Fact]
    public async Task The_mobile_client_loses_the_apps_earlier_redirect()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var earlier = new Uri("dev.simplarchive.mobile:/oauth2redirect");

        async Task<OpenIddictApplicationDescriptor> ReadAsync()
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, (await applications.FindByClientIdAsync(MobileClientSeeder.ClientId))!);
            return descriptor;
        }

        var seeded = await ReadAsync();
        seeded.RedirectUris.Add(earlier);
        await applications.UpdateAsync((await applications.FindByClientIdAsync(MobileClientSeeder.ClientId))!, seeded);

        await RetiredClients.RemoveAsync(applications);

        var after = await ReadAsync();
        Assert.DoesNotContain(earlier, after.RedirectUris);
        Assert.Contains(new Uri("com.alcifra.mobile:/oauth2redirect"), after.RedirectUris);
    }
}
