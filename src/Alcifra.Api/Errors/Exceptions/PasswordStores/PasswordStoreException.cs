namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// Base class for the password store's refusals (ADR 0964), so a caller may catch the group.
public abstract class PasswordStoreException : ApiException
{
    protected PasswordStoreException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
