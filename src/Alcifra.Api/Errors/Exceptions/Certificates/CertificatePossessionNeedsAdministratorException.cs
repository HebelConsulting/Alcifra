using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>
/// The tenant binds keys by possession, and the caller is not an administrator (ADR 1017). Without a proof that the
/// caller holds the key, enrolling a certificate would let anyone claim someone else's: whatever is sealed to them would
/// then be readable by the key's real holder. An administrator's enrolment vouches for the holder instead.
/// </summary>
public sealed class CertificatePossessionNeedsAdministratorException : CertificateException
{
    public CertificatePossessionNeedsAdministratorException()
        : base("CERTIFICATE_POSSESSION_NEEDS_ADMINISTRATOR", StatusCodes.Status403Forbidden,
            "This tenant binds keys to their holders by possession, so an administrator enrols a certificate for you. "
            + "Ask one to enrol yours.")
    {
    }
}
