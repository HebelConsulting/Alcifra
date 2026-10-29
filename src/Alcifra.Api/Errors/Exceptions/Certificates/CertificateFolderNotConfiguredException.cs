using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The tenant has no reader-certificate folder and no parent configured to create one under.</summary>
public sealed class CertificateFolderNotConfiguredException : CertificateException
{
    public CertificateFolderNotConfiguredException()
        : base("CERTIFICATE_NO_FOLDER", StatusCodes.Status409Conflict, "There is nowhere to file reader certificates: an administrator must choose the repository or folder they live under in the tenant settings. Its rights decide who can see whose certificate.")
    {
    }
}
