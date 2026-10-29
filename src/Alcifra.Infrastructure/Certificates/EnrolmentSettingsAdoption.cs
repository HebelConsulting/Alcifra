using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// Moves the Encryption Module's two enrolment settings into the tenant's own columns, once (ADR 0936): the core owns
/// enrolment now, and a module setting nobody declares any more would otherwise be a value nobody can see or change.
/// </summary>
/// <remarks>
/// <para>
/// <b>A MOVE, not a copy.</b> A copy has no way to know it already ran, so it would run at every start and put the
/// module's old value back over an administrator's later change. Deleting the module rows is what makes it once.
/// </para>
/// <para>
/// <b>The delete is the claim</b> (ADR 0836): both app instances run this at startup. Each reads the values, then deletes
/// the rows in one statement inside its transaction; only the instance whose delete removed them writes the columns.
/// </para>
/// </remarks>
public static class EnrolmentSettingsAdoption
{
    /// <summary>The module and keys the Encryption Module stored its enrolment settings under.</summary>
    public const string ModuleId = "encryption";

    public const string SelfServiceKey = "certificates.selfServiceEnabled";

    public const string ParentFolderKey = "certificates.parentFolderId";

    public static async Task AdoptEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger("Alcifra.Infrastructure.Certificates.EnrolmentSettingsAdoption");

        // Only once the core owns enrolment here: an Encryption Module older than ABI 1.17 still reads these settings
        // itself, and moving them away would switch its self-service off under it.
        var modules = root.GetService<IReadOnlyList<Modules.ModuleLoader.LoadedModule>>() ?? [];
        if (!modules.Any(m => m.Module.LicensesCertificateEnrolment))
        {
            return;
        }

        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().ModuleSettingValues
                .IgnoreQueryFilters(["TenantFilter"])
                .Where(v => v.ModuleId == ModuleId && (v.Key == SelfServiceKey || v.Key == ParentFolderKey))
                .Select(v => v.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenants)
        {
            try
            {
                await AdoptTenantAsync(root, tenantId, logger, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Moving the enrolment settings into tenant {TenantId} failed; the next start tries again", tenantId);
            }
        }
    }

    /// <summary>One tenant: read, claim by deleting, write. Returns whether this call moved them.</summary>
    public static async Task<bool> AdoptTenantAsync(IServiceProvider root, Guid tenantId, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var scope = root.CreateScope();
        ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var values = await db.ModuleSettingValues
            .AsNoTracking()
            .Where(v => v.ModuleId == ModuleId && (v.Key == SelfServiceKey || v.Key == ParentFolderKey))
            .ToListAsync(cancellationToken);
        var claimed = await db.ModuleSettingValues
            .Where(v => v.ModuleId == ModuleId && (v.Key == SelfServiceKey || v.Key == ParentFolderKey))
            .ExecuteDeleteAsync(cancellationToken);
        if (claimed == 0)
        {
            return false;   // the other instance moved them
        }

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        string? Value(string key) => values.FirstOrDefault(v => v.Key == key)?.Value;
        tenant.CertificateSelfServiceEnabled = bool.TryParse(Value(SelfServiceKey), out var on) && on;
        tenant.CertificateParentFolderId = Guid.TryParse(Value(ParentFolderKey), out var parent) ? parent : null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Moved the Encryption Module's enrolment settings into tenant {TenantId}: self-service {SelfService}, parent {ParentId}",
            tenantId, tenant.CertificateSelfServiceEnabled, tenant.CertificateParentFolderId);
        return true;
    }
}
