using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A CI/CD secret is made only through its store's own door (ADR 1084), as a password entry is (ADR 1035): the generic
// document door refuses it, named or implied by an unnamed item in a CI/CD store.
public sealed class CiCdSecretUsesItsStoreException : PasswordStoreException
{
    public CiCdSecretUsesItsStoreException()
        : base("CICD_SECRET_USES_ITS_STORE", StatusCodes.Status422UnprocessableEntity,
            "A CI/CD secret is created in its CI/CD store, not as a document. Use the store's own New secret.")
    {
    }
}
