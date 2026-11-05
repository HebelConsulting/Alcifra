using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>What came back is not what was sealed to the certificate, so the key was not shown (ADR 1061).</summary>
public sealed class CertificateChallengeWrongAnswerException : CertificateException
{
    public CertificateChallengeWrongAnswerException()
        : base("CERTIFICATE_CHALLENGE_WRONG_ANSWER", StatusCodes.Status403Forbidden,
            "That is not what was sealed to this certificate, so this does not prove you hold its key.")
    {
    }
}
