using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PushRegistrations;

// A registration needs an app id and a device label (each up to 100 characters), an https push address (up to 2000)
// and both Web Push keys: an uncompressed P-256 public key and a 16-byte auth secret, base64url (RFC 8291).
public sealed class InvalidPushRegistrationException() : PushRegistrationException(
    "INVALID_PUSH_REGISTRATION", StatusCodes.Status400BadRequest,
    "A push registration needs an app, a device label, an https push address and both Web Push keys.");
