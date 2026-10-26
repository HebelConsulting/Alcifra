using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Audience;

/// <summary>
/// An audience may only name readers (ADR 0983): a user with a grant of read, or a group with at least one such member.
/// The editor offers only the candidates the resource advertises; this is the same judgement for a request that did not.
/// </summary>
public sealed class AudienceMemberNotAReaderException : ContentAudienceException
{
    public AudienceMemberNotAReaderException()
        : base("AUDIENCE_MEMBER_NOT_A_READER", StatusCodes.Status400BadRequest,
            "Only people and groups who can read this can be in its audience. Grant read access first.")
    {
    }
}
