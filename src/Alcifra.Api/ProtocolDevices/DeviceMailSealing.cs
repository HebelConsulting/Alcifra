using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Modules;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>What an IMAP session seals its mail to, given the device that signed in (#1970, owner 2026-10-29/31).</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The organisation seals mail</b> (its mode answers <c>EnvelopesMail</c>), or the device is the
/// <b>earlier IMAP password</b>: exactly as IMAP always did, sealed to the reader's certificates when there are any.
/// The device's own setting is disregarded there (shown on and locked).</item>
/// <item><b>An enrolled device with "Encrypt mail content" on</b>: sealed. With no certificate to seal to, the login is
/// REFUSED with that reason: a device promised encryption never silently gets plaintext.</item>
/// <item><b>An enrolled device with it off</b>: plaintext.</item>
/// </list>
/// </remarks>
public static class DeviceMailSealing
{
    /// <summary>The certificates to seal to, or <c>Refused</c> when the device requires sealing and there is nothing to
    /// seal to.</summary>
    public static (ReaderCertificateSource Certificates, bool Refused) Decide(
        ProtocolDevice device, bool organisationSealsMail, ReaderCertificateSource resolved) =>
        (organisationSealsMail || device.Kind != ProtocolDeviceKind.Enrolled, device.EncryptMail) switch
        {
            (true, _) => (resolved, false),
            (false, true) => resolved.Envelopes ? (resolved, false) : (ReaderCertificateSource.None, true),
            (false, false) => (ReaderCertificateSource.None, false),
        };
}
