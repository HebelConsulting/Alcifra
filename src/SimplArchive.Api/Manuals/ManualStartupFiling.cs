using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Manuals;

/// <summary>
/// The filing moments that are not a request (ADR 0891): every startup, for every tenant — SimplArchive's own manual
/// and the manual of every module the tenant activated, and the client manuals an installation lays out for the
/// tenants it names (ADR 0934) — and a tenant's founding through the API.
/// </summary>
/// <remarks>
/// Each tenant runs in its own scope with its tenant set, so the tenant filter answers for it and one tenant's
/// tracked state never rides into the next. A failure is logged and the next tenant proceeds: arbitrary module code
/// (its manual's <c>Open</c>) runs here, and one bad package must not take the host down for everyone. Runs at the
/// END of the startup scope, after the seeders, so a tenant the demo seeders just founded gets its manuals on its
/// first start rather than its second.
/// </remarks>
public static class ManualStartupFiling
{
    /// <summary>The startup step: every tenant, the loaded modules, logged under the filer's category.</summary>
    public static Task FileEverywhereAsync(IServiceProvider root) =>
        FileEverywhereAsync(root,
            [.. root.GetRequiredService<IReadOnlyList<SimplArchive.Infrastructure.Modules.ModuleLoader.LoadedModule>>().Select(m => m.Module)],
            root.GetRequiredService<ILoggerFactory>().CreateLogger("SimplArchive.Api.Manuals.ManualFiler"));

    public static async Task FileEverywhereAsync(
        IServiceProvider root, IReadOnlyList<IIndustryModule> modules, ILogger logger, CancellationToken cancellationToken = default)
    {
        List<(Guid Id, string Name)> tenants;
        List<(Guid TenantId, string ModuleId)> activations;
        using (var scope = root.CreateScope())
        {
            // Before any request: no ambient tenant, so the tenant filter is bypassed BY NAME for these enumerations.
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            tenants = [.. (await db.Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(cancellationToken)).Select(t => (t.Id, t.Name))];
            activations = (await db.ModuleActivations.IgnoreQueryFilters(["TenantFilter"])
                    .Select(a => new { a.TenantId, a.ModuleId })
                    .ToListAsync(cancellationToken))
                .Select(a => (a.TenantId, a.ModuleId))
                .ToList();
        }

        var withManual = modules.Where(m => m.Manual is not null).ToDictionary(m => m.ModuleId, StringComparer.Ordinal);
        var (clientManuals, clientTenants) = ClientManuals(root.GetRequiredService<IConfiguration>(), logger);
        foreach (var (tenantId, name) in tenants)
        {
            var mine = activations.Where(a => a.TenantId == tenantId && withManual.ContainsKey(a.ModuleId))
                .Select(a => withManual[a.ModuleId])
                .ToList();
            await FileForTenantAsync(root, tenantId, mine, logger, cancellationToken);
            if (clientTenants.Contains(name))
            {
                await FileClientManualsAsync(root, tenantId, clientManuals, logger, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The client manuals an installation lays out (ADR 0934): every PDF in <c>Manuals:ClientDirectory</c>, for the
    /// tenants named in <c>Manuals:ClientTenants</c> (comma-separated names). Neither set: none. A directory that is
    /// configured but missing is a Warning, since nothing else will say why the manual never appears.
    /// </summary>
    internal static (IReadOnlyList<string> Paths, IReadOnlySet<string> Tenants) ClientManuals(IConfiguration configuration, ILogger logger)
    {
        var directory = configuration["Manuals:ClientDirectory"];
        var tenants = (configuration["Manuals:ClientTenants"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(directory) || tenants.Count == 0)
        {
            return ([], tenants);
        }

        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Manuals:ClientDirectory {Directory} does not exist; no client manual is filed", directory);
            return ([], tenants);
        }

        return ([.. Directory.EnumerateFiles(directory, "*.pdf").Order(StringComparer.Ordinal)], tenants);
    }

    /// <summary>Files the given client manuals for one tenant, each in a fresh scope, as the others are.</summary>
    public static async Task FileClientManualsAsync(
        IServiceProvider root, Guid tenantId, IReadOnlyList<string> paths, ILogger logger, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths)
        {
            await GuardedAsync(root, tenantId, Path.GetFileName(path), filer => filer.FileClientManualAsync(path, cancellationToken), logger);
        }
    }

    /// <summary>Files the core manual and the given modules' manuals for one tenant, each check in a fresh scope.</summary>
    public static async Task FileForTenantAsync(
        IServiceProvider root, Guid tenantId, IReadOnlyList<IIndustryModule> modules, ILogger logger, CancellationToken cancellationToken = default)
    {
        await GuardedAsync(root, tenantId, SimplArchive.Localization.Strings.ProductName, filer => filer.FileCoreManualAsync(cancellationToken), logger);
        // The archive signing keys (ADR 0961) beside the manuals: what an offline reader trusts, published where every
        // member already reads.
        var signing = root.GetRequiredService<OfflineArchives.ArchiveSigningKey>();
        await GuardedAsync(root, tenantId, "archive signing keys",
            filer => filer.FileArchiveSigningKeysAsync(signing.HistoryPem(), signing.Current?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) ?? string.Empty, cancellationToken),
            logger);
        foreach (var module in modules)
        {
            await GuardedAsync(root, tenantId, module.ModuleId, filer => filer.FileModuleManualAsync(module, cancellationToken), logger);
        }
    }

    private static async Task GuardedAsync(
        IServiceProvider root, Guid tenantId, string what, Func<ManualFiler, Task<ManualFilingOutcome>> file, ILogger logger)
    {
        try
        {
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            await file(scope.ServiceProvider.GetRequiredService<ManualFiler>());
        }
        catch (Exception e)
        {
            // Deliberately broad — the ModuleMaskBackfill posture.
            logger.LogError(e, "Filing the {What} manual failed for tenant {TenantId}", what, tenantId);
        }
    }
}
