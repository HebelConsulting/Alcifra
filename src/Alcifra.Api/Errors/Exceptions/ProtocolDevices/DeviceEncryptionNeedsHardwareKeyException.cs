using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// "Encrypt mail content" seals a device's mail to the user's hardware keys, so it is refused, with the reason, while
// the user has no attested hardware key the registry accepts (#1970, ADR 0963).
public sealed class DeviceEncryptionNeedsHardwareKeyException()
    : ProtocolDeviceException("DEVICE_ENCRYPTION_NEEDS_HARDWARE_KEY", StatusCodes.Status409Conflict,
        "Mail can be encrypted only to a hardware key, and none is enrolled for you. Enrol a hardware key first.");
