using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// A password entry's content must be shaped as a CMS envelope (ADR 0964, mirroring ADR 0940's module check): plaintext
// filed where a sealed secret belongs is refused before the version is confirmed, and the upload is removed.
public sealed class ContentNotSealedException : PasswordStoreException
{
    public ContentNotSealedException()
        : base("CONTENT_NOT_SEALED", StatusCodes.Status422UnprocessableEntity,
            "This document's content must be sealed to its readers (a CMS envelope); what was uploaded is not, so it was not filed.")
    {
    }
}
