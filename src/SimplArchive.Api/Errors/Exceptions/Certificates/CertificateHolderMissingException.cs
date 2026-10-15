using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>Neither the request nor the caller names whose certificate this is.</summary>
public sealed class CertificateHolderMissingException : CertificateException
{
    public CertificateHolderMissingException()
        : base("CERTIFICATE_NO_HOLDER", StatusCodes.Status400BadRequest, "This certificate has no holder: the caller has no e-mail address and none was given.")
    {
    }
}
