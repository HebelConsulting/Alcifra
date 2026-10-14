using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Asks the active module whether the phone's offline export may run on a tenant that refuses plaintext doors, and
/// whether its index data and chat travel sealed (ABI 1.15, ADR 0928).
/// </summary>
/// <remarks>
/// <para>
/// <b>No module, or none active, means refused</b>: on such a tenant nothing could envelope the content, so the export
/// is not offered, as every plaintext door is refused (ADR 0865). <b>A module that throws refuses too</b>, never "allow
/// by default": silence is not an answer.
/// </para>
/// <para>
/// Run <b>as the module</b>, like <see cref="ModuleReaderCertificates"/>: the setting is the module's own, per tenant.
/// </para>
/// </remarks>
public sealed class ModuleOfflineExportPolicy(
    SimplArchiveDbContext dbContext,
    IServiceProvider services,
    ILogger<ModuleOfflineExportPolicy> logger)
{
    private static readonly OfflineExportPolicy Refused = new(Allowed: false, SealIndexDataAndChat: false);

    public async Task<OfflineExportPolicy> ForTenantAsync(CancellationToken cancellationToken = default)
    {
        var modules = (services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>))
            as IReadOnlyList<ModuleLoader.LoadedModule> ?? [])
            .Where(m => m.Module.OfflineExportPolicy is not null)
            .ToList();
        var identity = services.GetService(typeof(ModuleIdentityAccessor)) as ModuleIdentityAccessor;
        var now = DateTimeOffset.UtcNow;

        foreach (var loaded in modules)
        {
            if (!await ModuleActivationCheck.IsActiveAsync(dbContext, loaded.Module.ModuleId, now, cancellationToken))
            {
                continue;
            }

            var restore = identity?.ModuleId;
            if (identity is not null)
            {
                identity.ModuleId = loaded.Module.ModuleId;
            }

            try
            {
                var answer = await loaded.Module.OfflineExportPolicy!(new OfflineExportPolicyContext(services));
                logger.LogDebug("Module {ModuleId} answered the offline export policy: allowed {Allowed}, sealed {Sealed}.",
                    loaded.Module.ModuleId, answer.Allowed, answer.SealIndexDataAndChat);
                return answer;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Module {ModuleId} failed to answer whether offline exports may run, so the export is refused.",
                    loaded.Module.ModuleId);
                return Refused;
            }
            finally
            {
                if (identity is not null)
                {
                    identity.ModuleId = restore;
                }
            }
        }

        logger.LogDebug("No active module answers the offline export policy; on a tenant that refuses plaintext doors the export is refused.");
        return Refused;
    }
}
