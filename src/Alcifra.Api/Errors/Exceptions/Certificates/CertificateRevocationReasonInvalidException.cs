using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>A revocation needs a reason: one of the four, or Other with a text of its own (#2278).</summary>
public sealed class CertificateRevocationReasonInvalidException : CertificateException
{
    public CertificateRevocationReasonInvalidException()
        : base("CERTIFICATE_REVOCATION_REASON_INVALID", StatusCodes.Status400BadRequest,
            "Say why the key is revoked: lost, compromised, replaced, left, or other with a short text (at most 200 characters).")
    {
    }
}
