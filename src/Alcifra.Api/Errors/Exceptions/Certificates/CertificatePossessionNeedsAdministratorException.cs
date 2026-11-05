using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>
/// The tenant binds keys by possession, and the caller is not an administrator and brought no proof (ADRs 1017 and 1061).
/// Without one, enrolling a certificate would let anyone claim someone else's: whatever is sealed to them would then be
/// readable by the key's real holder. A client answers this by running the challenge; an administrator's enrolment vouches
/// for the holder instead.
/// </summary>
public sealed class CertificatePossessionNeedsAdministratorException : CertificateException
{
    public CertificatePossessionNeedsAdministratorException()
        : base("CERTIFICATE_POSSESSION_NEEDS_ADMINISTRATOR", StatusCodes.Status403Forbidden,
            "This tenant binds keys to their holders by possession, so enrolling needs proof that you hold the key. "
            + "Update your client, or ask an administrator to enrol yours.")
    {
    }
}
