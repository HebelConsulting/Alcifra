using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// The address names a document that is not part of a password store (ADR 0964): a store, a folder in one, or an entry.
public sealed class NotAPasswordStoreItemException : PasswordStoreException
{
    public NotAPasswordStoreItemException()
        : base("NOT_A_PASSWORD_STORE_ITEM", StatusCodes.Status404NotFound, "There is no such password store, folder or entry.")
    {
    }
}
