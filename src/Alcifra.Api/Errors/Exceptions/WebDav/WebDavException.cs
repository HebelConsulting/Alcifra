namespace Alcifra.Api.Errors.Exceptions.WebDav;

// Base class for WebDAV gateway errors, translated to an RFC 7807 response by the global handler. See the exception-type
// principle in CLAUDE.md.
public abstract class WebDavException : ApiException
{
    protected WebDavException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
