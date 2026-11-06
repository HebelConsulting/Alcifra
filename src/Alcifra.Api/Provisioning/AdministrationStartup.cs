using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// At every start, each active tenant gets its <c>Administration</c> root with <c>Users</c> beneath, and <c>Users</c> is
/// brought in step with the live personal spaces (ADR 1066). Idempotent, per tenant and guarded, in the manuals' posture:
/// one tenant's failure is logged and the next is still served.
/// </summary>
public static class AdministrationStartup
{
    public static async Task EnsureEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdministrationStartup));
        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = root.CreateScope();
                scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
                await AdministrationFolders.EnsureAsync(scope.ServiceProvider.GetRequiredService<AlcifraDbContext>(), tenantId, logger, cancellationToken);
            }
            catch (Exception e)
            {
                // Deliberately broad — the ModuleMaskBackfill posture: a start never fails on one tenant's folder.
                logger.LogError(e, "The Administration folder could not be ensured for tenant {TenantId}", tenantId);
            }
        }
    }
}
