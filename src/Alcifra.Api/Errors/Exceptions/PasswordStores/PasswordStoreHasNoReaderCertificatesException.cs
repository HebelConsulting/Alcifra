using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A service account asked for a new entry in a store none of whose readers holds an attested certificate (ADR 0982):
// nothing could be sealed to anybody. Refused before anything is written.
public sealed class PasswordStoreHasNoReaderCertificatesException : PasswordStoreException
{
    public PasswordStoreHasNoReaderCertificatesException()
        : base("PASSWORD_STORE_NO_READER_CERTIFICATES", StatusCodes.Status403Forbidden,
            "Nobody who reads this password store has an enrolled hardware key yet, so a new entry could not be sealed to anybody.")
    {
    }
}
