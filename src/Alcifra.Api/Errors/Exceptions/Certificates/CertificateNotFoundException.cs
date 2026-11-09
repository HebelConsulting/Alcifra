using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>No enrolled certificate of this tenant has this id (#2278).</summary>
public sealed class CertificateNotFoundException : CertificateException
{
    public CertificateNotFoundException()
        : base("CERTIFICATE_NOT_FOUND", StatusCodes.Status404NotFound, "There is no such enrolled certificate.")
    {
    }
}
