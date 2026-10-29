using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.AttestationRoots;

/// <summary>Somebody other than a platform administrator asked for the installation's trusted attestation roots.</summary>
public sealed class AttestationRootsPlatformOnlyException : AttestationRootException
{
    public AttestationRootsPlatformOnlyException()
        : base("ATTESTATION_ROOTS_PLATFORM_ONLY", StatusCodes.Status403Forbidden,
            "Only a platform administrator decides which attestation roots the installation trusts. A tenant administrator "
            + "may switch some of them off for the tenant, through the certificate settings.")
    {
    }
}
