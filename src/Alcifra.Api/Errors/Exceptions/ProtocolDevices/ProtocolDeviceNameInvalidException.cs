using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// A device needs a name of 1 to 100 characters (#1970).
public sealed class ProtocolDeviceNameInvalidException()
    : ProtocolDeviceException("PROTOCOL_DEVICE_NAME_INVALID", StatusCodes.Status400BadRequest,
        "A device needs a name of 1 to 100 characters.");
