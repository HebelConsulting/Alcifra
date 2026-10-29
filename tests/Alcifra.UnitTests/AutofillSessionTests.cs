using OpenIddict.Abstractions;
using Alcifra.Api.Authentication;
using Alcifra.Api.Provisioning;
using Alcifra.Domain.Tenants;

namespace Alcifra.UnitTests;

// ADR 0981: the rules of the autofill session exchange, each on its own. The end-to-end proof that the issued tokens
// work and live independently is AutofillSessionTests in the end-to-end suite.
public class AutofillSessionTests
{
    [Fact]
    public void Only_the_mobile_apps_sign_in_client_may_open_an_autofill_session_today()
    {
        // The mobile app's id, and its id before the rename until v0.56.0 (WireNames.Legacy).
        Assert.Equal(
            [MobileClientSeeder.ClientId, Alcifra.Presentation.WireNames.Legacy.MobileClientId],
            AutofillSession.SubjectClients.Order());
        Assert.All(AutofillClientSeeder.ClientIds, id => Assert.DoesNotContain(id, AutofillSession.SubjectClients));   // no chaining
    }

    [Fact]
    public void A_mobile_users_own_token_is_accepted() =>
        Assert.Null(AutofillSession.RefusalForToken(MobileClientSeeder.ClientId, isUser: true, isImpersonating: false));

    [Theory]
    [InlineData(null)]
    [InlineData("simplarchive-desktop")]
    [InlineData("alcifra-desktop")]
    [InlineData("blazor-client")]
    [InlineData("simplarchive-mobile-autofill")]
    [InlineData("alcifra-mobile-autofill")]
    [InlineData("SIMPLARCHIVE-MOBILE")]
    [InlineData("ALCIFRA-MOBILE")]
    public void A_token_from_any_other_client_is_refused(string? clientId) =>
        Assert.NotNull(AutofillSession.RefusalForToken(clientId, isUser: true, isImpersonating: false));

    [Fact]
    public void A_token_that_is_not_an_interactive_users_is_refused() =>
        Assert.NotNull(AutofillSession.RefusalForToken(MobileClientSeeder.ClientId, isUser: false, isImpersonating: false));

    [Fact]
    public void An_impersonation_token_is_refused() =>
        Assert.NotNull(AutofillSession.RefusalForToken(MobileClientSeeder.ClientId, isUser: true, isImpersonating: true));

    [Fact]
    public void An_active_user_in_an_active_tenant_may_have_a_session() =>
        Assert.Null(AutofillSession.RefusalForAccount(userFound: true, userIsActive: true, TenantStatus.Active));

    [Theory]
    [InlineData(false, true, TenantStatus.Active)]
    [InlineData(true, false, TenantStatus.Active)]
    [InlineData(true, true, TenantStatus.Deactivated)]
    public void A_missing_or_deactivated_user_or_an_inactive_tenant_is_refused(bool found, bool active, TenantStatus status) =>
        Assert.NotNull(AutofillSession.RefusalForAccount(found, active, status));

    // The client can do nothing but the exchange, renewal and revocation: no authorization endpoint, so it can never be
    // signed in interactively, and no redirect URI to send a code to.
    [Fact]
    public void The_autofill_client_may_exchange_renew_and_revoke_and_nothing_else() =>
        Assert.Equal(
            new[]
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Prefixes.GrantType + "urn:ietf:params:oauth:grant-type:token-exchange",
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
                OpenIddictConstants.Permissions.Endpoints.Revocation,
            }.Order(),
            AutofillClientSeeder.Permissions.Order());
}
