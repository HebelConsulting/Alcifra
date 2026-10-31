using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Repairs seeded e-mails on an EXISTING demo volume that were filed before auto-classification ran (#1272).
/// </summary>
/// <remarks>
/// <para>
/// <b>The general defect is the idempotent seed itself.</b> A seeder that only ever touches rows it just
/// created strands every volume that already exists, so any improvement to seeded data — richer metadata,
/// a corrected date, a new field — reaches only volumes created afterwards. The same lesson as #574's
/// credential heal, and the reason that one is written the way it is.
/// </para>
/// <para>
/// <b>How it surfaced.</b> The demo's e-mail showed a document date with NO TIME, which is not what an
/// e-mail looks like. It was not a seeder bug: a fresh volume produces the eMail mask, five index fields
/// and the time from the message header. The volume predated the seeder being routed through
/// <c>DocumentFinalizer</c> at all, so the row had never been classified and never would be.
/// </para>
/// <para>
/// <b>Scope is narrow on purpose, because a heal can destroy data.</b> Only documents in the demo tenant
/// whose latest version is an <c>.eml</c>/<c>.msg</c>, which still carry a GENERIC mask AND have NO index
/// values at all — a state fresh seeding cannot produce and a person is most unlikely to have created.
/// Everything it then writes is machine-derived from the message itself. A heal that re-applied the seeder
/// wholesale would be a data-loss bug wearing a fix's clothes.
/// </para>
/// <para>
/// Demo-only by construction: only <see cref="DemoDataSeeder"/> calls it, and only when <c>Demo:*</c> is
/// configured. It is independent of which samples the demo files (#1788): it repairs whatever mail a long-lived
/// volume already holds.
/// </para>
/// </remarks>
internal static class DemoEmailHeal
{
    internal static async Task HealUnclassifiedEmailsAsync(
        IServiceProvider services, AlcifraDbContext dbContext, string demoTenantName,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Alcifra.Api.Provisioning.DemoDataSeeder");

        var tenantId = await dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"])
            .Where(t => t.Name == demoTenantName).Select(t => t.Id).SingleOrDefaultAsync(cancellationToken);
        if (tenantId == Guid.Empty)
        {
            return;
        }

        var candidates = await dbContext.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId
                && v.Status == DocumentVersionStatus.Confirmed
                && (v.ObjectKey.EndsWith(".eml") || v.ObjectKey.EndsWith(".msg"))
                && !dbContext.FieldValues.Any(f => f.DocumentId == v.DocumentId))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        // THE TENANT HAS TO BE ESTABLISHED FIRST, and this is where the heal would have failed at startup.
        // Every ITenantScoped read goes through the tenant query filter, and on this path — an EXISTING volume
        // — nothing has set the accessor. Without this the finalizer's own document lookup matches zero rows and
        // throws "Sequence contains no elements" from inside classification, which reads as a corrupt document
        // rather than as a missing ambient value.
        var tenants = services.GetRequiredService<CurrentTenantAccessor>();
        var previous = tenants.TenantId;
        tenants.TenantId = tenantId;
        try
        {
            var finalizer = services.GetRequiredService<Documents.DocumentFinalizer>();
            var healed = 0;
            foreach (var version in candidates)
            {
                // The generic-mask check is asked HERE rather than in the query because it is a rule about
                // well-known mask ids resolved per tenant, not a column — and AutoClassifyAsync asks it again
                // anyway, so a document classified since simply returns false and is skipped.
                if (await finalizer.ReclassifyAsync(version, cancellationToken))
                {
                    healed++;
                }
            }

            if (healed > 0)
            {
                logger.LogInformation(
                    "Healed {Count} seeded e-mail(s) on an existing demo volume: they were filed before "
                    + "auto-classification ran, so they carried no mask and no index data (#1272).",
                    healed);
            }
        }
        finally
        {
            // Restored rather than left set: this runs during startup, in a scope other steps share, and an
            // ambient tenant nobody set deliberately is how a later step silently reads the wrong rows.
            tenants.TenantId = previous;
        }
    }
}
