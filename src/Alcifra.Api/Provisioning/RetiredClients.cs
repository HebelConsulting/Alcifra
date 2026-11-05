using OpenIddict.Abstractions;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Takes the client ids from before the rename off an installation that still has them (ADR 1064). No longer seeding one
/// is not enough: its row would go on signing people in. Removing a client removes its authorizations and tokens with it,
/// so a session held under an old id ends here, and the API refuses that token at its next request (ADR 0895).
/// </summary>
/// <remarks>
/// Retirement trigger (ADR 0885): delete this class once every installation has started a release carrying it.
/// </remarks>
public static class RetiredClients
{
    internal static readonly string[] ClientIds =
    [
        "simplarchive-desktop",
        "simplarchive-mobile",
        "simplarchive-mobile-autofill",
        "saconsole",
        "sapass",
    ];

    // The heal only adds, so the mobile client still lists the app's earlier redirect on an installation seeded before.
    internal const string MobileRedirectUri = "dev.simplarchive.mobile:/oauth2redirect";

    public static async Task RemoveAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default)
    {
        foreach (var clientId in ClientIds)
        {
            if (await applications.FindByClientIdAsync(clientId, cancellationToken) is { } retired)
            {
                await applications.DeleteAsync(retired, cancellationToken);
            }
        }

        if (await applications.FindByClientIdAsync(MobileClientSeeder.ClientId, cancellationToken) is { } mobile)
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, mobile, cancellationToken);
            if (descriptor.RedirectUris.Remove(new Uri(MobileRedirectUri)))
            {
                await applications.UpdateAsync(mobile, descriptor, cancellationToken);
            }
        }
    }
}
