using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Audience;

/// <summary>
/// The audience was asked of a document whose mask declares none (ADR 0983).
/// </summary>
public sealed class NoAudienceException : ContentAudienceException
{
    public NoAudienceException()
        : base("NO_AUDIENCE", StatusCodes.Status404NotFound,
            "This item has no audience: it is not encrypted content.")
    {
    }
}
