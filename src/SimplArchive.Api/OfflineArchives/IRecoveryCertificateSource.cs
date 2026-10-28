using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.OfflineArchives;

/// <summary>
/// The current tenant's recovery certificate for offline exports (ADR 0960): the PEM of its public certificate, or null
/// when none is set — which refuses every offline export.
/// </summary>
/// <remarks>A seam rather than a column read in the exporter, so the archive format is tested without a tenant row.</remarks>
public interface IRecoveryCertificateSource
{
    Task<string?> CurrentPemAsync(CancellationToken cancellationToken);
}

/// <summary>The tenant's setting, <c>Tenant.OfflineExportRecoveryCertificatePem</c>, set with alcifra (#1695).</summary>
public sealed class TenantRecoveryCertificateSource(SimplArchiveDbContext dbContext, ICurrentTenantAccessor tenant) : IRecoveryCertificateSource
{
    public async Task<string?> CurrentPemAsync(CancellationToken cancellationToken) =>
        await dbContext.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.OfflineExportRecoveryCertificatePem).SingleOrDefaultAsync(cancellationToken);
}
