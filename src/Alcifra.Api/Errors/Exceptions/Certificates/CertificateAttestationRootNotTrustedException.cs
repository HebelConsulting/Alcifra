using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>
/// The attestation chains to no root this tenant trusts: one the installation does not know, has switched off, or this
/// tenant has switched off (ADR 0963).
/// </summary>
public sealed class CertificateAttestationRootNotTrustedException : CertificateException
{
    public CertificateAttestationRootNotTrustedException(string reason)
        : base("CERTIFICATE_ATTESTATION_ROOT_NOT_TRUSTED", StatusCodes.Status400BadRequest,
            $"The key's attestation does not chain to an attestation root trusted here: {reason} The platform administrator decides "
            + "which roots the installation trusts.")
    {
    }
}
