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

    // Rename phase 4 (#1117): every client is registered under its alcifra id AND the id before the rename, with the
    // same permissions and redirects, so a client installed before the rename signs in unchanged until v0.57.0.
    [Theory]
    [InlineData(Alcifra.Presentation.WireNames.DesktopClientId, Alcifra.Presentation.WireNames.Legacy.DesktopClientId)]
    [InlineData(Alcifra.Presentation.WireNames.MobileClientId, Alcifra.Presentation.WireNames.Legacy.MobileClientId)]
    [InlineData(Alcifra.Presentation.WireNames.MobileAutofillClientId, Alcifra.Presentation.WireNames.Legacy.MobileAutofillClientId)]
    [InlineData(Alcifra.Presentation.WireNames.CliClientId, Alcifra.Presentation.WireNames.Legacy.CliClientId)]
    [InlineData(Alcifra.Presentation.WireNames.PassClientId, Alcifra.Presentation.WireNames.Legacy.PassClientId)]
    public async Task A_client_is_registered_under_its_alcifra_id_and_its_id_before_the_rename_alike(string clientId, string legacyClientId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var current = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(current, await applications.FindByClientIdAsync(clientId) ?? throw new Xunit.Sdk.XunitException($"{clientId} is not registered"));
        var legacy = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(legacy, await applications.FindByClientIdAsync(legacyClientId) ?? throw new Xunit.Sdk.XunitException($"{legacyClientId} is not registered"));

        Assert.Equal(current.Permissions.Order(), legacy.Permissions.Order());
        Assert.Equal(current.RedirectUris.Select(u => u.ToString()).Order(), legacy.RedirectUris.Select(u => u.ToString()).Order());
    }

    [Fact]
    public async Task The_mobile_client_accepts_the_apps_redirect_before_and_after_its_identifier_moves()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, (await applications.FindByClientIdAsync(MobileClientSeeder.ClientId))!);

        Assert.Contains(new Uri("com.alcifra.mobile:/oauth2redirect"), descriptor.RedirectUris);
        Assert.Contains(new Uri("dev.simplarchive.mobile:/oauth2redirect"), descriptor.RedirectUris);
    }
}
