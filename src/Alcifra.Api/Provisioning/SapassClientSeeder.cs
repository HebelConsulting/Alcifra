using OpenIddict.Abstractions;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Seeds the OpenIddict client for <c>apass</c>, the password-store CLI (ADR 0964; apass decisions 2026-10-25).
/// </summary>
/// <remarks>
/// Its own file beside <see cref="SaConsoleClientSeeder"/>, and for its reason: one client per file keeps
/// <c>Program.cs</c> under the line guard, and keeps "who may sign in this way" readable as a list rather than a
/// block in a startup method.
/// </remarks>
public static class SapassClientSeeder
{
    public const string ClientId = Alcifra.Presentation.WireNames.PassClientId;

    /// <summary>Idempotent: creates the client when it is absent, and asks nothing of an existing one.</summary>
    public static async Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default)
    {
        // The id before the rename too, while installed tools still send it (WireNames.Legacy, retired at v0.56.0).
        await SeedAsync(applications, ClientId, cancellationToken);
        await SeedAsync(applications, Alcifra.Presentation.WireNames.Legacy.PassClientId, cancellationToken);
    }

    private static async Task SeedAsync(IOpenIddictApplicationManager applications, string clientId, CancellationToken cancellationToken)
    {
        if (await applications.FindByClientIdAsync(clientId, cancellationToken) is not null)
        {
            return;
        }

        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,

            // A PUBLIC client with no redirect URI: the device grant never redirects, which is what lets the tool
            // sign in over SSH on a host with no browser (RFC 8628, as alcifra does — ADR 0823).
            //
            // Explicit consent, as alcifra's: a device grant hands a token to a machine the approving person may
            // not be sitting at, so the approval is an act rather than an inference. The page names THIS tool, which
            // is the whole reason it has a client id of its own instead of borrowing the administrator's.
            ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
            DisplayName = $"{Alcifra.Localization.Strings.ProductName} password CLI (apass)",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.DeviceAuthorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.DeviceCode,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
            },
        }, cancellationToken);
    }
}
