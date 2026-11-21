namespace Alcifra.Api.Errors.Exceptions.Branding;

// Base class for runtime-branding errors (ADR 1103, #2408): the caller's tenant wears no pack, or the palette a
// self-service choice names is not one the current brand carries. Inherits from ApiException so the global handler
// writes RFC 7807; concrete errors inherit from this so a caller can catch (BrandingException) for the whole area.
public abstract class BrandingException : ApiException
{
    protected BrandingException(string errorCode, int statusCode, string message, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(errorCode, statusCode, message, extensions)
    {
    }
}
