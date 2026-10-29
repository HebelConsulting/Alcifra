using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PushRegistrations;

// The push address is already registered to another user: refused, or anyone who learned a device's address could
// aim their own notifications at it.
public sealed class PushEndpointTakenException() : PushRegistrationException(
    "PUSH_ENDPOINT_TAKEN", StatusCodes.Status409Conflict, "This push address is registered to another user.");
