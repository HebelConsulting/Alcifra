using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The tenant does not bind keys by possession, so there is nothing to prove this way (ADR 1061).</summary>
public sealed class CertificateChallengeNotNeededException : CertificateException
{
    public CertificateChallengeNotNeededException()
        : base("CERTIFICATE_CHALLENGE_NOT_NEEDED", StatusCodes.Status409Conflict,
            "This tenant does not bind keys by possession, so enrol the certificate directly.")
    {
    }
}
