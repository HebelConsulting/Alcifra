using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// A finalize asked for a system comment kind it may not set (ADR 0964): only a password entry's re-seal carries one.
public sealed class InvalidVersionCommentKindException : PasswordStoreException
{
    public InvalidVersionCommentKindException(int kind)
        : base("INVALID_VERSION_COMMENT_KIND", StatusCodes.Status400BadRequest,
            $"The version comment kind {kind} cannot be set here: only a password entry's re-seal carries a system comment.")
    {
    }
}
