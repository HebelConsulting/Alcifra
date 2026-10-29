using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// Runs <see cref="PasswordEntryHistory"/> over every active tenant (ADR 0964): superseded entry versions deleted after
/// their undo window, entries a revoked certificate can read marked exposed. In both app instances, which is why every
/// act inside claims its item first (ADR 0836).
/// </summary>
public sealed class PasswordHistorySweepWorker(IServiceScopeFactory scopeFactory, ILogger<PasswordHistorySweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <summary>Often enough that a revocation reaches the stores within minutes, rarely enough to cost nothing.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The password history sweep failed; it retries in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One pass over every active tenant, each in its own scope with its tenant set.</summary>
    public async Task<PasswordEntryHistory.Outcome> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<Guid> tenants;
        using (var scope = scopeFactory.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(cancellationToken);
        }

        var total = new PasswordEntryHistory.Outcome(0, 0);
        foreach (var tenantId in tenants)
        {
            using var scope = scopeFactory.CreateScope();
            ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
            var outcome = await scope.ServiceProvider.GetRequiredService<PasswordEntryHistory>().SweepAsync(now, cancellationToken);
            if (outcome.MarkedExposed > 0 || outcome.VersionsDeleted > 0 || outcome.AbandonedRemoved > 0)
            {
                logger.LogInformation("Password history of tenant {TenantId}: {Exposed} entr(ies) marked exposed, {Deleted} superseded version(s) deleted, "
                    + "{Abandoned} entr(ies) without content removed", tenantId, outcome.MarkedExposed, outcome.VersionsDeleted, outcome.AbandonedRemoved);
            }

            total += outcome;
        }

        return total;
    }
}
