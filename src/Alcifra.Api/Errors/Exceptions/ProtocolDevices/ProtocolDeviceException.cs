namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// Base class for a user's protocol devices (#1970): the per-device passwords for IMAP, WebDAV, CalDAV and CardDAV.
// Inherits from ApiException so the global handler answers RFC 7807; concrete errors inherit from this.
public abstract class ProtocolDeviceException : ApiException
{
    protected ProtocolDeviceException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
