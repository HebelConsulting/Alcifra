using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// A device's chosen collections name only shared calendars and address books the user can see (#2090): anything else is
// refused rather than stored and silently never shown.
public sealed class ProtocolDeviceCollectionUnknownException()
    : ProtocolDeviceException("PROTOCOL_DEVICE_COLLECTION_UNKNOWN", StatusCodes.Status422UnprocessableEntity,
        "A chosen collection is not a shared calendar or address book you can see. Reload the device and choose again.");
