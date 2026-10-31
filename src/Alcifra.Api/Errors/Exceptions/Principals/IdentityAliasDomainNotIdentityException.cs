using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Principals;

// An identity alias lives on one of the tenant's VERIFIED identity domains (ADR 1015): the domain is what the tenant
// vouches for, so an address on a domain it does not hold would let Strict binding accept anybody's certificate.
public sealed class IdentityAliasDomainNotIdentityException(string address)
    : PrincipalException("IDENTITY_ALIAS_DOMAIN_NOT_IDENTITY", StatusCodes.Status400BadRequest,
        $"'{address}' is not on one of this tenant's verified identity domains. Register and verify the domain first, "
        + "with the identity role.");
