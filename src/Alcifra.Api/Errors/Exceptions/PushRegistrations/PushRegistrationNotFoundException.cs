using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PushRegistrations;

// No such registration of the caller's: another user's is not even acknowledged to exist.
public sealed class PushRegistrationNotFoundException() : PushRegistrationException(
    "PUSH_REGISTRATION_NOT_FOUND", StatusCodes.Status404NotFound, "No such push registration.");
