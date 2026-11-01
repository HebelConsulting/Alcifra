using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// A device's name is unique per user (#1970), so the list says which device is which.
public sealed class ProtocolDeviceNameTakenException(string name)
    : ProtocolDeviceException("PROTOCOL_DEVICE_NAME_TAKEN", StatusCodes.Status409Conflict,
        $"You already have a device named '{name}'.");
