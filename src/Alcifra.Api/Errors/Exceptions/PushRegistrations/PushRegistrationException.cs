namespace Alcifra.Api.Errors.Exceptions.PushRegistrations;

// The refusals of a phone's push registration (SimplArchiveMobile #20, slice B).
public abstract class PushRegistrationException(string errorCode, int statusCode, string message)
    : ApiException(errorCode, statusCode, message);
