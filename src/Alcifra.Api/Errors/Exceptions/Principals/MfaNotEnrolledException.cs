using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Principals;

public sealed class MfaNotEnrolledException : PrincipalException
{
    public MfaNotEnrolledException()
        : base("MFA_NOT_ENROLLED", StatusCodes.Status400BadRequest, "Start enrollment before enabling two-factor authentication.")
    {
    }
}
