using Microsoft.Extensions.Logging;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Presentation;
using SimplArchive.ModuleAbi;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// Asks every module active for the tenant for its part in a device's setup profile, and tells them of a revoked device
/// (ABI 1.27, ADR 1036). The module runs as its own principal, as a booking review does (ADR 0781).
/// </summary>
/// <remarks>
/// A module that fails while the profile is built fails the hand-over, by name: a profile missing what the person was
/// promised is worse than none. A module that fails on a revocation is logged and left to its own retry; the core's
/// revocation has already committed.
/// </remarks>
public sealed class ModuleDeviceProfiles(AlcifraDbContext dbContext, IServiceProvider services, ILogger<ModuleDeviceProfiles> logger)
{
    public async Task<IReadOnlyList<ExtraPayload>> PayloadsAsync(
        Guid tenantId, Guid userId, string userEmail, Guid deviceId, string deviceName, string webHost, CancellationToken cancellationToken)
    {
        var payloads = new List<ExtraPayload>();
        foreach (var (loaded, seam) in await ActiveAsync(tenantId, cancellationToken))
        {
            var context = new DeviceProfileContext(tenantId, userId, userEmail, deviceId, deviceName, webHost,
                part => ConfigurationProfile.PayloadUuid(deviceId, $"{loaded.Module.ModuleId}.{part}"), services);
            try
            {
                foreach (var payload in await AsModuleAsync(loaded, () => seam.PayloadsAsync(context, cancellationToken)))
                {
                    payloads.Add(new ExtraPayload(payload.Type, $"{loaded.Module.ModuleId}.{payload.Part}", payload.DisplayName, payload.BodyXml));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Module {ModuleId} failed to add its payloads to device {DeviceId}'s profile; the hand-over is refused. Trace carries the exchange (ADR 0626).",
                    loaded.Module.ModuleId, deviceId);
                throw new ModuleDeviceProfileFailedException(loaded.Module.ModuleId, exception);
            }
        }

        return payloads;
    }

    public async Task DeviceRevokedAsync(Guid tenantId, Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var context = new DeviceRevokedContext(tenantId, userId, deviceId, services);
        foreach (var (loaded, seam) in await ActiveAsync(tenantId, cancellationToken))
        {
            try
            {
                await AsModuleAsync(loaded, async () =>
                {
                    await seam.DeviceRevokedAsync(context, cancellationToken);
                    return true;
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Module {ModuleId} failed to act on the revocation of device {DeviceId}; the device is revoked in the core, the module retries on its own.",
                    loaded.Module.ModuleId, deviceId);
            }
        }
    }

    private async Task<List<(ModuleLoader.LoadedModule Loaded, IModuleDeviceProfiles Seam)>> ActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var active = new List<(ModuleLoader.LoadedModule, IModuleDeviceProfiles)>();
        var modules = services.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>)) as IReadOnlyList<ModuleLoader.LoadedModule> ?? [];
        var now = DateTimeOffset.UtcNow;
        foreach (var loaded in modules)
        {
            if (loaded.Module.DeviceProfiles is { } seam && await ModuleActivationCheck.IsActiveAsync(dbContext, loaded.Module.ModuleId, now, cancellationToken))
            {
                active.Add((loaded, seam));
            }
        }

        return active;
    }

    // The module acts as ITSELF (ADR 0736): what it reads to decide its payloads is gated by its own principal.
    private async Task<T> AsModuleAsync<T>(ModuleLoader.LoadedModule loaded, Func<Task<T>> act)
    {
        var identity = services.GetService(typeof(ModuleIdentityAccessor)) as ModuleIdentityAccessor;
        var restore = identity?.ModuleId;
        if (identity is not null)
        {
            identity.ModuleId = loaded.Module.ModuleId;
        }

        try
        {
            return await act();
        }
        finally
        {
            if (identity is not null)
            {
                identity.ModuleId = restore;
            }
        }
    }
}

/// <summary>A module could not add its payloads; the hand-over is refused naming it (ADR 1036).</summary>
public sealed class ModuleDeviceProfileFailedException(string moduleId, Exception inner)
    : Exception($"Module '{moduleId}' could not add its part to the device profile.", inner)
{
    public string ModuleId { get; } = moduleId;
}
