namespace Alcifra.Api.Errors.Exceptions.Inspection;

// Base class for a seal inspection's refusals (#2291), so a caller may catch the group.
public abstract class SealInspectionException : ApiException
{
    protected SealInspectionException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
