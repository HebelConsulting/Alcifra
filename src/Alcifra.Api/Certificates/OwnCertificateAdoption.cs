using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Certificates;

/// <summary>
/// At startup, before the registry's rebuild: every tenant's users' own certificates carried into the registry, once
/// (#1839, <see cref="CertificateEnrolment.AdoptOwnCertificatesAsync"/>). Only while a loaded module licenses enrolment —
/// otherwise the column is still what is asked, and nothing needs carrying. Each tenant in its own scope; a failure is
/// logged and the next tenant proceeds.
/// </summary>
public static class OwnCertificateAdoption
{
    public static async Task AdoptEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OwnCertificateAdoption));
        var modules = root.GetService<IReadOnlyList<ModuleLoader.LoadedModule>>() ?? [];
        if (!modules.Any(m => m.Module.LicensesCertificateEnrolment))
        {
            return;
        }

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
                if (await scope.ServiceProvider.GetRequiredService<CertificateEnrolment>().AdoptOwnCertificatesAsync(cancellationToken) is > 0 and var adopted)
                {
                    logger.LogInformation("Carried {Count} users' own certificates into the certificate registry of tenant {TenantId}", adopted, tenantId);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Carrying users' own certificates into the registry failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
