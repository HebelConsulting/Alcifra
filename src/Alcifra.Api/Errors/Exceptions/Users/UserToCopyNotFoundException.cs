using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Users;

// A user created as a copy of another (#2277) names a source that is not a user of this tenant: nothing is created.
public sealed class UserToCopyNotFoundException : UsersException
{
    public UserToCopyNotFoundException()
        : base("USER_TO_COPY_NOT_FOUND", StatusCodes.Status404NotFound, "The user to copy does not exist in this organisation.")
    {
    }
}
