using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>The attestation sent with a certificate cannot be read, or a signature in it does not verify (ADR 0963).</summary>
public sealed class CertificateAttestationInvalidException : CertificateException
{
    public CertificateAttestationInvalidException(string reason)
        : base("CERTIFICATE_ATTESTATION_INVALID", StatusCodes.Status400BadRequest, $"The key's attestation is not valid: {reason}")
    {
    }
}
