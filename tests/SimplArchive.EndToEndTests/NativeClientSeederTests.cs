using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SimplArchive.Api.Provisioning;
using SimplArchive.Api.Security;

namespace SimplArchive.EndToEndTests;

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
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType, healed.Permissions);   // impersonation stays a desktop act

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
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType, descriptor.Permissions);
    }
}
