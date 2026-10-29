using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>
/// A certificate was enrolled without the attestation of its key, on a tenant that requires one (ADR 0963): enrol it from
/// the token with the desktop client or <c>alcifra certificates enrol --attestation</c>.
/// </summary>
public sealed class CertificateNotAttestedException : CertificateException
{
    public CertificateNotAttestedException()
        : base("CERTIFICATE_NOT_ATTESTED", StatusCodes.Status400BadRequest,
            "This tenant accepts only certificates whose key is attested to have been generated on a hardware token, and none "
            + "was sent. Enrol it from the token itself (the desktop client reads the attestation), or with alcifra and the "
            + "token's attestation files.")
    {
    }
}
