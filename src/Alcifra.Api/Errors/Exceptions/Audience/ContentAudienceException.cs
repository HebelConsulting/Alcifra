namespace Alcifra.Api.Errors.Exceptions.Audience;

// Base class for encrypted content's audience refusals (ADR 0983), so a caller may catch the group.
public abstract class ContentAudienceException : ApiException
{
    protected ContentAudienceException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
