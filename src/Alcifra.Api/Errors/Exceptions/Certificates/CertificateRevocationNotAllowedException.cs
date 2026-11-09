using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>Only a key's holder, or an administrator who manages users, may revoke it (#2278).</summary>
public sealed class CertificateRevocationNotAllowedException : CertificateException
{
    public CertificateRevocationNotAllowedException()
        : base("CERTIFICATE_REVOCATION_NOT_ALLOWED", StatusCodes.Status403Forbidden,
            "Only the key's holder, or an administrator who manages users, may revoke it.")
    {
    }
}
