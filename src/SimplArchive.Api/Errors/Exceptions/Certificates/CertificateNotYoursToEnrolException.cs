using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>A caller who is not an administrator named somebody else as the holder.</summary>
public sealed class CertificateNotYoursToEnrolException : CertificateException
{
    public CertificateNotYoursToEnrolException()
        : base("CERTIFICATE_NOT_YOURS_TO_ENROL", StatusCodes.Status403Forbidden, "Only an administrator can enrol a certificate for somebody else.")
    {
    }
}
