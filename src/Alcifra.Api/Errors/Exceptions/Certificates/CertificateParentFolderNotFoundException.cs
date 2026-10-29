using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>The tenant setting names a parent for the reader-certificate folder that is not a live document of this tenant.</summary>
public sealed class CertificateParentFolderNotFoundException : CertificateException
{
    public CertificateParentFolderNotFoundException(Guid parentId)
        : base("CERTIFICATE_PARENT_NOT_FOUND", StatusCodes.Status400BadRequest,
            $"{parentId} is not a repository or folder of this tenant, so reader certificates cannot be filed under it.")
    {
    }
}
