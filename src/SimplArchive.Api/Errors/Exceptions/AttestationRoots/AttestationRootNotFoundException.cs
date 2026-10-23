using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.AttestationRoots;

/// <summary>No trusted attestation root has this id.</summary>
public sealed class AttestationRootNotFoundException : AttestationRootException
{
    public AttestationRootNotFoundException(Guid id)
        : base("ATTESTATION_ROOT_NOT_FOUND", StatusCodes.Status404NotFound, $"There is no attestation root {id}.")
    {
    }
}
