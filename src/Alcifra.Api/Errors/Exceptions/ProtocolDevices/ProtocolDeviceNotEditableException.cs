using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.ProtocolDevices;

// A password from before per-device enrolment keeps working until it is revoked, and is not changed otherwise (#1970,
// owner 2026-10-29/31): it reaches what it always reached, and its mail behaves as IMAP always did.
public sealed class ProtocolDeviceNotEditableException()
    : ProtocolDeviceException("PROTOCOL_DEVICE_NOT_EDITABLE", StatusCodes.Status409Conflict,
        "An earlier password can only be revoked. Enrol a device to choose its protocols and mail encryption.");
