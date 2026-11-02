using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A password entry is made only through its store's own door, which carries ADR 0982's rule that its author can read it
// back (#1931, owner decision 2026-11-02): the generic document door refuses it, named or implied by an unnamed item in
// a store or password folder.
public sealed class PasswordEntryUsesItsStoreException : PasswordStoreException
{
    public PasswordEntryUsesItsStoreException()
        : base("PASSWORD_ENTRY_USES_ITS_STORE", StatusCodes.Status422UnprocessableEntity,
            "A password entry is created in its password store, not as a document. Use the store's own New entry.")
    {
    }
}
