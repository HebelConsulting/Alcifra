using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// How long a person's session in the command-line tools lasts (ADR 1071): it ends after 15 idle minutes, because every
/// command renews it, and at the latest 12 hours after the login, however busy. Held here rather than in the tools, since a
/// copied session file is exactly what the limit is for. The desktop and the phone keep their own lifetimes (ADR 0083).
/// </summary>
public static class CliSessionPolicy
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan HardLimit = TimeSpan.FromHours(12);

    /// <summary>What the two tools' clients need for it, healed onto clients registered before it.</summary>
    public static readonly string[] Permissions =
    [
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        OpenIddictConstants.Permissions.Endpoints.Revocation,
    ];

    // Travels in the refresh token only: a claim with no destination never reaches an access token.
    internal const string StartedClaim = "alcifra_cli_session_started";

    public static bool Governs(string? clientId) => clientId is SaConsoleClientSeeder.ClientId or PassClientSeeder.ClientId;

    /// <summary>A new login: the hard limit counts from here.</summary>
    public static void Begin(ClaimsPrincipal principal, DateTimeOffset now)
    {
        principal.SetClaim(StartedClaim, now.ToString("O", CultureInfo.InvariantCulture));
        Limit(principal, HardLimit);
    }

    /// <summary>A renewal: false once the hard limit has passed, or where the token does not say when it began.</summary>
    public static bool TryContinue(ClaimsPrincipal principal, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(principal.GetClaim(StartedClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
            || now - started >= HardLimit)
        {
            return false;
        }

        Limit(principal, HardLimit - (now - started));
        return true;
    }

    // Neither token may outlive the hard limit.
    private static void Limit(ClaimsPrincipal principal, TimeSpan remaining)
    {
        principal.SetRefreshTokenLifetime(remaining < IdleLimit ? remaining : IdleLimit);
        if (remaining < IdleLimit)
        {
            principal.SetAccessTokenLifetime(remaining);
        }
    }
}
