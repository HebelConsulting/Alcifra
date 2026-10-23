using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>The attestation is genuine but attests a different key than the certificate being enrolled (ADR 0963).</summary>
public sealed class CertificateAttestationKeyMismatchException : CertificateException
{
    public CertificateAttestationKeyMismatchException()
        : base("CERTIFICATE_ATTESTATION_KEY_MISMATCH", StatusCodes.Status400BadRequest,
            "The attestation attests a different key than this certificate's. Send the attestation of the slot the certificate is in.")
    {
    }
}
