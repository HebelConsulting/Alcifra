using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>A tenant's attestation-root selection names a root the installation does not have (ADR 0963).</summary>
public sealed class CertificateAttestationRootUnknownException : CertificateException
{
    public CertificateAttestationRootUnknownException(Guid rootId)
        : base("CERTIFICATE_ATTESTATION_ROOT_UNKNOWN", StatusCodes.Status400BadRequest,
            $"There is no attestation root {rootId} in this installation; only a listed root can be switched off.")
    {
    }
}
