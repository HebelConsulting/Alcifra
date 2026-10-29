using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The certificate does not meet the tenant's policy, or a policy being saved would not work as written.</summary>
public sealed class CertificatePolicyRefusedException : CertificateException
{
    public CertificatePolicyRefusedException(string reason)
        : base("CERTIFICATE_POLICY_REFUSED", StatusCodes.Status400BadRequest, reason)
    {
    }
}
