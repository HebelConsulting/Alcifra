using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// A password folder or entry is created with a name (ADR 0964): it is the one thing about it in the clear.
public sealed class PasswordItemNameRequiredException : PasswordStoreException
{
    public PasswordItemNameRequiredException()
        : base("PASSWORD_ITEM_NAME_REQUIRED", StatusCodes.Status400BadRequest, "A password folder or entry needs a name.")
    {
    }
}
