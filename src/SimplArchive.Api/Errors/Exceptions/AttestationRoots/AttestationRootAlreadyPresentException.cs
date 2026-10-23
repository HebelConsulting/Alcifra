using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.AttestationRoots;

/// <summary>The uploaded root is already one of the installation's attestation roots.</summary>
public sealed class AttestationRootAlreadyPresentException : AttestationRootException
{
    public AttestationRootAlreadyPresentException(string name)
        : base("ATTESTATION_ROOT_ALREADY_PRESENT", StatusCodes.Status409Conflict,
            $"This root is already listed, as '{name}'. Switch that entry on instead of uploading it again.")
    {
    }
}
