using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

// A mask names the file types it holds, and this is not one (ABI 1.29, ADR 1046). 415 and its own code, because the
// caller's remedy is another file, not another name or another folder.
public sealed class ContentExtensionNotAcceptedException : DocumentException
{
    public ContentExtensionNotAcceptedException(string message)
        : base("CONTENT_EXTENSION_NOT_ACCEPTED", StatusCodes.Status415UnsupportedMediaType, message)
    {
    }
}
