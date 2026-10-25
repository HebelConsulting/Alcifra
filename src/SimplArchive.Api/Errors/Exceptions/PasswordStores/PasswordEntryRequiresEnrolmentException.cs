using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

// A person who reads the store but holds no attested certificate asked for a new entry (ADR 0982): it would be sealed
// to the other readers only, so its author could never open what they saved. Refused before anything is written.
public sealed class PasswordEntryRequiresEnrolmentException : PasswordStoreException
{
    public PasswordEntryRequiresEnrolmentException()
        : base("PASSWORD_ENTRY_ENROLMENT_REQUIRED", StatusCodes.Status403Forbidden,
            "You have no enrolled hardware key a new entry could be sealed to, so you could not open what you save. Enrol a hardware key first.")
    {
    }
}
