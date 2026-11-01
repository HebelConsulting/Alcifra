namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// Removes expired device hand-overs (#1970 slice 2): a profile carrying a password stays at most a few minutes past its
/// link's lifetime. In both app instances; the delete is idempotent and has no side effect, so it needs no claim.
/// </summary>
public sealed class DeviceHandOverSweepWorker(IServiceScopeFactory scopeFactory, ILogger<DeviceHandOverSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, stoppingToken);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var removed = await scope.ServiceProvider.GetRequiredService<DeviceHandOvers>().SweepAsync(stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogDebug("Removed {Count} expired device hand-over(s)", removed);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The device hand-over sweep failed; it retries in {Interval}", Interval);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
