using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>A caller who is not an administrator tried to replace the certificate policy.</summary>
public sealed class CertificatePolicyNotYoursToSetException : CertificateException
{
    public CertificatePolicyNotYoursToSetException()
        : base("CERTIFICATE_POLICY_NOT_YOURS_TO_SET", StatusCodes.Status403Forbidden, "Only an administrator can change the certificate policy.")
    {
    }
}
