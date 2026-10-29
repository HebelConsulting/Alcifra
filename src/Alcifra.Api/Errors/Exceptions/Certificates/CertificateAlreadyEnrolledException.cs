using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The certificate is already enrolled in this tenant, for the caller or for somebody else.</summary>
public sealed class CertificateAlreadyEnrolledException : CertificateException
{
    public CertificateAlreadyEnrolledException(string message)
        : base("CERTIFICATE_ALREADY_ENROLLED", StatusCodes.Status409Conflict, message)
    {
    }
}
