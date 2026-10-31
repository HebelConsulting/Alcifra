using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.MailRouting;

// A domain registered for nothing (ADR 1015): neither account addresses live on it nor is mail received for it. That
// is a domain to remove, not one to keep, so the request says so rather than storing a row that does nothing.
public sealed class MailDomainNeedsARoleException(string domain)
    : MailRoutingException("MAIL_DOMAIN_NEEDS_A_ROLE", StatusCodes.Status400BadRequest,
        $"'{domain}' must be an identity domain, a receiving domain, or both. To stop using it, remove it.");
