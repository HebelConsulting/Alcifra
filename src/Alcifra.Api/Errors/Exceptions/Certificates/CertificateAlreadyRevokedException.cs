using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The certificate is revoked already: a revocation is final, and its date and reason stand (#2278).</summary>
public sealed class CertificateAlreadyRevokedException : CertificateException
{
    public CertificateAlreadyRevokedException()
        : base("CERTIFICATE_ALREADY_REVOKED", StatusCodes.Status409Conflict, "This certificate is revoked already.")
    {
    }
}
