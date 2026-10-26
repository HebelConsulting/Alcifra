using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Audience;

/// <summary>
/// Managers are named people, never groups (ADR 0983): joining a group must not quietly confer the power to change who decrypts.
/// </summary>
public sealed class AudienceManagerMustBeAUserException : ContentAudienceException
{
    public AudienceManagerMustBeAUserException()
        : base("AUDIENCE_MANAGER_MUST_BE_A_USER", StatusCodes.Status400BadRequest,
            "An audience manager must be a person, not a group.")
    {
    }
}
