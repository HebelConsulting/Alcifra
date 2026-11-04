using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.WebDav;

// Thrown when the shared lock store cannot answer (#2176, ADR 1052): a lock nobody can enforce must not be handed out, and a
// write must not pass a check nobody could make.
public sealed class WebDavLocksUnavailableException : WebDavException
{
    public WebDavLocksUnavailableException()
        : base("WEBDAV_LOCKS_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable,
            "File locks cannot be checked right now. Try again shortly.")
    {
    }
}
