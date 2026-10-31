using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.MailRouting;

// Users' identity aliases live on this domain (ADR 1015). Taking its identity role away, or removing it, would leave
// those addresses on a domain the tenant no longer vouches for, and Strict certificate binding would go on accepting
// certificates for them. The aliases go first, deliberately by hand: each is somebody's address.
public sealed class MailDomainHasIdentityAliasesException(string domain, int count)
    : MailRoutingException("MAIL_DOMAIN_HAS_IDENTITY_ALIASES", StatusCodes.Status409Conflict,
        $"{count} identity alias{(count == 1 ? string.Empty : "es")} of this tenant's users {(count == 1 ? "is" : "are")} on "
        + $"'{domain}'. Remove {(count == 1 ? "it" : "them")} first.");
