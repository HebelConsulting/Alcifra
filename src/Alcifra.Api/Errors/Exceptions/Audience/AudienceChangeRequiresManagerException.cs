using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Audience;

/// <summary>
/// Somebody who is not an effective audience manager tried to change an audience, or to re-seal a Sealed document to a
/// new one (ADR 0983, replacing ADR 0975's author-only rule).
/// </summary>
public sealed class AudienceChangeRequiresManagerException : ContentAudienceException
{
    public AudienceChangeRequiresManagerException()
        : base("AUDIENCE_CHANGE_REQUIRES_MANAGER", StatusCodes.Status403Forbidden,
            "Only an audience manager can change who this is encrypted for.")
    {
    }
}
