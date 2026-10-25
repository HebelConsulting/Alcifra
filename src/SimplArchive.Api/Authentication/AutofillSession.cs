using SimplArchive.Api.Provisioning;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Api.Authentication;

/// <summary>
/// The rules of the autofill session exchange (ADR 0981): a signed-in app trades its access token for a SEPARATE
/// authorization of <see cref="AutofillClientSeeder.ClientId"/>, so the extension renews in its own process and never
/// presents the app's rotating refresh token.
/// </summary>
/// <remarks>
/// Pure decisions, kept apart from the token endpoint so each rule is tested on its own. The exchange is a different
/// act from impersonation (ADR 0354): the same user, another client, a new authorization; nothing here grants a
/// right the subject token did not carry.
/// </remarks>
public static class AutofillSession
{
    /// <summary>
    /// The clients whose access token may be exchanged: an app's own interactive sign-in client. Today the mobile app
    /// alone. A future app with its own sign-in client is added HERE, in this one list, or gets an autofill client of
    /// its own (ADR 0981). The autofill client is deliberately absent, so its session cannot chain into another.
    /// </summary>
    public static readonly IReadOnlySet<string> SubjectClients = new HashSet<string>(StringComparer.Ordinal)
    {
        MobileClientSeeder.ClientId,
    };

    /// <summary>Why a validated subject token may not be exchanged, or null when it may.</summary>
    /// <param name="subjectClientId">The client the subject token was issued to.</param>
    /// <param name="isUser">Whether the token belongs to an interactive user (not a service account or administrator).</param>
    /// <param name="isImpersonating">Whether the token is an impersonation token (ADR 0354).</param>
    public static string? RefusalForToken(string? subjectClientId, bool isUser, bool isImpersonating) =>
        (subjectClientId, isUser, isImpersonating) switch
        {
            (null, _, _) => "the subject token names no client",
            _ when !SubjectClients.Contains(subjectClientId) => $"the subject token was issued to {subjectClientId}, which may not open an autofill session",
            (_, false, _) => "the subject token is not an interactive user's",
            (_, _, true) => "an impersonation token cannot open an autofill session",
            _ => null,
        };

    /// <summary>Why the token's user may not get a session now, or null when they may — the question a renewal asks.</summary>
    public static string? RefusalForAccount(bool userFound, bool userIsActive, TenantStatus tenantStatus) =>
        (userFound, userIsActive, tenantStatus) switch
        {
            (false, _, _) => "the user no longer exists",
            (_, false, _) => "the user is deactivated",
            (_, _, not TenantStatus.Active) => "the tenant is not active",
            _ => null,
        };
}
