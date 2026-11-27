using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

// The caller's idSlug (#2501) is present but unusable: it is what derives the document's id, so an empty or absurdly
// long slug is the caller's mistake and nothing is created.
public sealed class InvalidDocumentIdSlugException()
    : DocumentException(
        "INVALID_ID_SLUG",
        StatusCodes.Status400BadRequest,
        "An id slug must be 1 to 200 characters (it is trimmed first). It names the document's identity, not its name.");
