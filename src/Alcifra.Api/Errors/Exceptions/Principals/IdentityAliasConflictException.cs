using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Principals;

// An address names at most one person in a tenant (ADR 1015): this one is already a user's address or an alias.
// Which user is not said; the administrator asking can look it up, and the refusal stays the same either way.
public sealed class IdentityAliasConflictException(string address)
    : PrincipalException("IDENTITY_ALIAS_CONFLICT", StatusCodes.Status409Conflict,
        $"'{address}' is already the address or an identity alias of a user in this tenant.");
