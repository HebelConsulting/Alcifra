using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// Seeds and heals the public client of the mobile app's autofill extension (ADR 0981): no redirect URI, no secret,
/// and only two grants. Its session is never signed in interactively; the app exchanges its own access token for it
/// (<see cref="SimplArchive.Api.Authentication.AutofillSession"/>), and from then on it renews and revokes on its own.
/// </summary>
/// <remarks>
/// A separate authorization because the extension runs in its own process: sharing the app's rotating refresh token
/// across two processes is a race that, past the reuse leeway, revokes the whole authorization (#1869).
/// </remarks>
public static class AutofillClientSeeder
{
    public const string ClientId = SimplArchive.Presentation.WireNames.MobileAutofillClientId;

    /// <summary>Both ids that open an autofill session, while the one before the rename is accepted (until v0.56.0).</summary>
    public static readonly IReadOnlySet<string> ClientIds = new HashSet<string>(StringComparer.Ordinal)
    {
        ClientId,
        SimplArchive.Presentation.WireNames.Legacy.MobileAutofillClientId,
    };

    internal static readonly string[] Permissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.Prefixes.GrantType + SimplArchive.Auth.ImpersonationConstants.TokenExchangeGrantType,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        OpenIddictConstants.Permissions.Endpoints.Revocation,
    ];

    public static Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default) =>
        PublicClientSeeder.SeedAsync(applications, new PublicClientRegistration(ClientId, Permissions, [], SimplArchive.Presentation.WireNames.Legacy.MobileAutofillClientId), cancellationToken);
}
