using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// Rebuilds the core's certificate registry from the Certificate documents at startup (#1790, ADR 0935): every
/// tenant, every live certificate document projected again, every row whose document is gone removed. Idempotent,
/// so a restart is harmless; and the way existing enrolments (filed while the Encryption Module kept its own
/// projection) reach the registry without a data migration.
/// </summary>
/// <remarks>
/// Only while a loaded module encrypts to users (one licensing enrolment, ABI 1.17, or answering reader certificates
/// itself): otherwise nothing reads the registry and nothing should pay for it. Each tenant in its own scope with its
/// tenant set, like the manual filing; a failure is logged and the next tenant proceeds.
/// </remarks>
public static class CertificateRegistryRebuild
{
    public static async Task RebuildEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger("Alcifra.Infrastructure.Certificates.CertificateRegistry");
        var modules = root.GetService<IReadOnlyList<ModuleLoader.LoadedModule>>() ?? [];
        if (!modules.Any(m => m.Module.LicensesCertificateEnrolment || m.Module.ReaderCertificates is not null))
        {
            logger.LogDebug("Certificate registry rebuild skipped: no loaded module encrypts to users");
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("Certificate registry rebuild: starting");

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
                await RebuildTenantAsync(root, tenantId, logger, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Rebuilding the certificate registry failed for tenant {TenantId}", tenantId);
            }
        }

        logger.LogInformation("Certificate registry rebuilt for {Count} tenant(s) in {Elapsed} ms", tenants.Count, started.ElapsedMilliseconds);
    }

    /// <summary>One tenant: project every certificate document, drop the rows of documents that are gone.</summary>
    public static async Task RebuildTenantAsync(IServiceProvider root, Guid tenantId, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var scope = root.CreateScope();
        ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

        var documents = await db.Documents
            .Where(d => db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Certificate))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
        foreach (var documentId in documents)
        {
            await CertificateRegistryProjection.ProjectAsync(db, storage, documentId, tenantId, logger, cancellationToken);
        }

        // Rows whose document is no longer a live certificate (deleted, purged, re-masked): removed.
        var stale = await db.EnrolledCertificates.Where(c => !documents.Contains(c.DocumentId)).ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            db.EnrolledCertificates.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Certificate registry of tenant {TenantId}: {Count} certificate document(s) projected", tenantId, documents.Count);

        // Certificates that address nobody because their key is not attested to a root trusted here (ADR 0963): said once
        // per start, because their holders are told only by the listing, and an administrator may need to act.
        var trust = await Attestation.AttestationTrust.ForTenantAsync(db, tenantId, cancellationToken);
        var unaddressed = (await db.EnrolledCertificates.AsNoTracking().Where(c => c.RevokedAt == null)
            .Select(c => c.AttestationRootThumbprint).ToListAsync(cancellationToken)).Count(t => !trust.Addresses(t));
        if (unaddressed > 0)
        {
            logger.LogWarning(
                "Tenant {TenantId} has {Count} enrolled certificate(s) whose key is not attested to a trusted root; nothing new is sealed "
                + "to them until their holders enrol them again with the key's attestation (ADR 0963)", tenantId, unaddressed);
        }
    }
}
