using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Certificates;

/// <summary>
/// At startup, before the registry's rebuild: every tenant's users' own certificates carried into the registry, once
/// (#1839, <see cref="CertificateEnrolment.AdoptOwnCertificatesAsync"/>), and every user whose certificate then does not
/// address them told so, once. On every installation since the column retired (#1970 slice 4). Each tenant in its own
/// scope; a failure is logged and the next tenant proceeds.
/// </summary>
public static class OwnCertificateAdoption
{
    public static async Task AdoptEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OwnCertificateAdoption));
        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = root.CreateScope();
                ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
                var (carried, notified) = await scope.ServiceProvider.GetRequiredService<CertificateEnrolment>()
                    .AdoptOwnCertificatesAsync(scope.ServiceProvider.GetRequiredService<INotificationService>(), cancellationToken);
                if (carried + notified > 0)
                {
                    logger.LogInformation("Carried {Carried} users' own certificates into the certificate registry of tenant {TenantId}; "
                        + "told {Notified} users theirs no longer addresses them", carried, tenantId, notified);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Carrying users' own certificates into the registry failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
