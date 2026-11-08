using Microsoft.Extensions.Configuration;

namespace Alcifra.Api.Security;

/// <summary>
/// The public demo's own login, shown on its sign-in page (ADR 1075, Mobile #275): a visitor from an app store, the web or
/// the desktop otherwise reaches a sign-in page with no way to learn the demo's password. Shown only where
/// <c>DemoSignIn:Email</c> and <c>DemoSignIn:Password</c> are both set, which is the kiosk alone, on both its instances
/// (the seeding <c>Demo:*</c> settings stay on one): the readiness gate refuses them in production (ADR 0343), so no
/// customer's or vendor's page can carry it.
/// </summary>
public sealed record DemoSignInHint(string Email, string Password)
{
    /// <summary>The configured hint, or null where the page shows none.</summary>
    public static DemoSignInHint? From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration["DemoSignIn:Email"] is { Length: > 0 } email
            && configuration["DemoSignIn:Password"] is { Length: > 0 } password
                ? new DemoSignInHint(email, password)
                : null;
    }
}
