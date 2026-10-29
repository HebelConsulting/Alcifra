using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Audience;

/// <summary>
/// The change would leave no effective manager (ADR 0983): the last manager cannot be removed.
/// </summary>
public sealed class AudienceNeedsAManagerException : ContentAudienceException
{
    public AudienceNeedsAManagerException()
        : base("AUDIENCE_NEEDS_A_MANAGER", StatusCodes.Status409Conflict,
            "The audience must keep at least one manager who can read it.")
    {
    }
}
