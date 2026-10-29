using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// My Passwords belongs to a user's personal space (ADR 0964); a service account has none.
public sealed class PasswordStoreNotAvailableException : PasswordStoreException
{
    public PasswordStoreNotAvailableException()
        : base("PASSWORD_STORE_NOT_AVAILABLE", StatusCodes.Status403Forbidden, "Only a signed-in user has a personal password store.")
    {
    }
}
