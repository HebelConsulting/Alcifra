using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The tenant has not switched on self-service enrolment, and the caller is not an administrator.</summary>
public sealed class CertificateSelfServiceDisabledException : CertificateException
{
    public CertificateSelfServiceDisabledException()
        : base("CERTIFICATE_SELF_SERVICE_DISABLED", StatusCodes.Status403Forbidden, "This tenant does not allow enrolling your own certificate. An administrator can enrol it for you, or switch on self-service in the tenant settings.")
    {
    }
}
