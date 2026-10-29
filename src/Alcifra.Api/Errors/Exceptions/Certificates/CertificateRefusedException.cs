using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The certificate fails enrolment's own checks (a private key in the file, an unreadable chain, a key that cannot receive an envelope).</summary>
public sealed class CertificateRefusedException : CertificateException
{
    public CertificateRefusedException(string reason)
        : base("CERTIFICATE_REFUSED", StatusCodes.Status400BadRequest, reason)
    {
    }
}
