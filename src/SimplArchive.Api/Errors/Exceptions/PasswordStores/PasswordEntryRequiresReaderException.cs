using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// A person who may create in the store without being one of its readers asked for a new entry (ADR 0982) — typically a
// tenant administrator through the bypass, which makes nobody a reader (ADR 0964). Refused before anything is written.
public sealed class PasswordEntryRequiresReaderException : PasswordStoreException
{
    public PasswordEntryRequiresReaderException()
        : base("PASSWORD_ENTRY_NOT_A_READER", StatusCodes.Status403Forbidden,
            "You are not a reader of this password store, so a new entry would not be sealed to you. Grant yourself read access to the store first.")
    {
    }
}
