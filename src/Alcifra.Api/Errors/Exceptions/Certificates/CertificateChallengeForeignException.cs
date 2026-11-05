using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The token was damaged, or was issued to somebody else or in another tenant (ADR 1061).</summary>
public sealed class CertificateChallengeForeignException : CertificateException
{
    public CertificateChallengeForeignException()
        : base("CERTIFICATE_CHALLENGE_FOREIGN", StatusCodes.Status403Forbidden,
            "This proof was not issued to you. Ask for a new challenge.")
    {
    }
}
