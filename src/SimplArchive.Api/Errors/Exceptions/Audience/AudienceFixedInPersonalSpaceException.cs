using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Audience;

/// <summary>
/// An item in a personal space has its owner as its only member and manager, computed (ADR 0983); nothing is stored to edit.
/// </summary>
public sealed class AudienceFixedInPersonalSpaceException : ContentAudienceException
{
    public AudienceFixedInPersonalSpaceException()
        : base("AUDIENCE_FIXED_IN_PERSONAL_SPACE", StatusCodes.Status409Conflict,
            "This is in a personal space, so only its owner can decrypt it; its audience cannot be changed.")
    {
    }
}
