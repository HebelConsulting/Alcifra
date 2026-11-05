using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The challenge's five minutes have passed (ADR 1061).</summary>
public sealed class CertificateChallengeExpiredException : CertificateException
{
    public CertificateChallengeExpiredException()
        : base("CERTIFICATE_CHALLENGE_EXPIRED", StatusCodes.Status403Forbidden,
            "This challenge has expired. Ask for a new one.")
    {
    }
}
