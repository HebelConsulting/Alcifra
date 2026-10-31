using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Registers a mail domain that the stack's own CONFIGURATION declares — already verified (#667, ADR 0692).
/// </summary>
/// <remarks>
/// <para>
/// Delivery accepts only a verified domain, and the ordinary way to become one is a DNS TXT challenge. A
/// declared domain skips it, and that is not a loophole: the operator writing it into the configuration that
/// also creates this tenant and its administrator has already asserted more than a TXT record proves. It is
/// also the only workable answer for the demo and kiosk stacks, which own no zone to publish into.
/// </para>
/// <para>
/// No token is stored. There was never a challenge to answer, and a token sitting on a verified row would
/// suggest something outstanding — an administrator would go looking for a record to publish.
/// </para>
/// <para>
/// One helper rather than a copy per seeder: the rule (idempotent, normalized, verified-on-arrival) is the
/// same wherever a domain is declared, and the second copy is where it would start to differ.
/// </para>
/// </remarks>
public static class DeclaredMailDomain
{
    /// <summary>Registers <paramref name="domain"/> for the tenant if it is not already registered.</summary>
    /// <param name="receiving">
    /// False declares an identity-only domain (ADR 1015): account addresses live on it, no mail is routed to it, and
    /// another tenant may hold it too.
    /// </param>
    /// <returns>True when a row was added — false when it was already there, or the name is unusable.</returns>
    public static async Task<bool> EnsureAsync(
        AlcifraDbContext dbContext,
        Guid tenantId,
        string? domain,
        DateTimeOffset now,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        bool receiving = true)
    {
        var trimmed = (domain ?? string.Empty).Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(trimmed))
        {
            return false; // not configured at all — a stack that does not receive mail simply omits it
        }

        if (!Documents.MailDomainName.IsWellFormed(trimmed))
        {
            // Warning: the operator asked for something and got nothing, and nothing else will ever tell them.
            // Mail then fails much later, as "550 no such recipient", with no thread back to this line.
            logger?.LogWarning(
                "Configured mail domain '{Domain}' is not a domain name and was ignored; no mail will be "
                + "accepted for it. Enter the domain part on its own, for example example.com.", trimmed);
            return false;
        }

        // IgnoreQueryFilters: provisioning runs at startup with no current tenant, so the tenant filter would
        // match nothing and this would re-add the row on every boot.
        var normalized = trimmed.ToUpperInvariant();
        if (await dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(d => d.TenantId == tenantId && d.NormalizedDomain == normalized, cancellationToken))
        {
            return false; // already ours: nothing to do, and re-adding would violate the per-tenant unique index
        }

        // A RECEIVING domain is unique across the installation, so that check is global — a domain another tenant
        // already receives for must not be claimed here, and finding out by way of a constraint violation would take
        // the whole startup down. An identity-only domain routes nothing and may sit on several tenants (ADR 1015).
        if (receiving && await dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(d => d.NormalizedDomain == normalized && d.IsReceiving, cancellationToken))
        {
            // The dangerous silence. The operator declared a domain, another tenant already holds it, and
            // without this the stack starts, looks healthy, and refuses every message for a reason nothing
            // reports (ADR 0626).
            logger?.LogWarning(
                "Configured mail domain '{Domain}' is already registered to a different tenant and was NOT "
                + "claimed for {TenantId}; no mail will be accepted for it here.", trimmed, tenantId);
            return false;
        }

        dbContext.TenantMailDomains.Add(new TenantMailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Domain = trimmed,
            CreatedAt = now,
            VerifiedAt = now,
            IsIdentity = true,
            IsReceiving = receiving,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        // A lifecycle milestone: from here the tenant receives mail (or holds accounts on the domain), without anyone
        // having published anything.
        logger?.LogInformation(
            "Mail domain {Domain} declared by configuration for tenant {TenantId} and marked verified ({Roles}).",
            trimmed, tenantId, receiving ? "identity and receiving" : "identity only");

        return true;
    }
}
