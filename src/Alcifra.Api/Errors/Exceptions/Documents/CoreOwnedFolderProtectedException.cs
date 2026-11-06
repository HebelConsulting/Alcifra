using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

// The Administration root and its Users folder are the archive's own (ADR 1066): not deleted, renamed or moved, and
// nothing is placed in them by hand. Its own wire code because no right is missing and no name clashes — there is simply
// nothing for the caller to do differently.
public sealed class CoreOwnedFolderProtectedException : DocumentException
{
    public CoreOwnedFolderProtectedException(string message)
        : base("CORE_OWNED_FOLDER_PROTECTED", StatusCodes.Status409Conflict, message)
    {
    }
}
