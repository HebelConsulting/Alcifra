using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The challenge was issued for another certificate than the one being enrolled (ADR 1061).</summary>
public sealed class CertificateChallengeOtherCertificateException : CertificateException
{
    public CertificateChallengeOtherCertificateException()
        : base("CERTIFICATE_CHALLENGE_OTHER_CERTIFICATE", StatusCodes.Status403Forbidden,
            "This proof is for another certificate. Ask for a new challenge for this one.")
    {
    }
}
