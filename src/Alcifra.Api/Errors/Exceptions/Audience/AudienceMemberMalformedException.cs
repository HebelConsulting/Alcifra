using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Audience;

/// <summary>
/// A row of an audience <c>PUT</c> named neither or both of a user and a group (ADR 0983): exactly one is the rule the
/// table's CHECK holds, said here before the database has to.
/// </summary>
public sealed class AudienceMemberMalformedException : ContentAudienceException
{
    public AudienceMemberMalformedException()
        : base("AUDIENCE_MEMBER_MALFORMED", StatusCodes.Status400BadRequest,
            "Each audience member must name exactly one user or one group.")
    {
    }
}
