using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.AttestationRoots;

/// <summary>An uploaded attestation root is not a single public CA certificate.</summary>
public sealed class AttestationRootInvalidException : AttestationRootException
{
    public AttestationRootInvalidException(string reason)
        : base("ATTESTATION_ROOT_INVALID", StatusCodes.Status400BadRequest, $"This is not an attestation root that can be trusted: {reason}")
    {
    }
}
