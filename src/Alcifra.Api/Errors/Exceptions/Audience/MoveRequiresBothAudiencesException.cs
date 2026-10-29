using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Audience;

/// <summary>
/// A folder or entry moving to another password store must be re-sealed to the target's audience (ADR 0983), which only
/// somebody in both audiences can do.
/// </summary>
public sealed class MoveRequiresBothAudiencesException : ContentAudienceException
{
    public MoveRequiresBothAudiencesException()
        : base("MOVE_REQUIRES_BOTH_AUDIENCES", StatusCodes.Status403Forbidden,
            "Moving this into another password store needs you to be in both stores' audiences, so you can encrypt it for the new one.")
    {
    }
}
