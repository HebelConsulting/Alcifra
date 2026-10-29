using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Principals;

public sealed class InvalidMfaCodeException : PrincipalException
{
    public InvalidMfaCodeException()
        : base("INVALID_MFA_CODE", StatusCodes.Status400BadRequest, "The authentication code is incorrect.")
    {
    }
}
