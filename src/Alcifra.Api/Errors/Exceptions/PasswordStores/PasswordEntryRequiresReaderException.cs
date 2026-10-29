using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A person who may create in the store without being in its effective audience asked for a new entry (ADR 0982, the
// audience since ADR 0983) — a reader the managers did not add, or a tenant administrator through the bypass, which
// makes nobody a member. Refused before anything is written. The code keeps its released name for released clients.
public sealed class PasswordEntryRequiresReaderException : PasswordStoreException
{
    public PasswordEntryRequiresReaderException()
        : base("PASSWORD_ENTRY_NOT_A_READER", StatusCodes.Status403Forbidden,
            "You are not in this password store's audience, so a new entry would not be encrypted for you. Ask one of its audience managers to add you.")
    {
    }
}
