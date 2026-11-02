using System.Threading.RateLimiting;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Modules;

/// <summary>Which tenant serves a module's anonymous prefix on a host (ABI 1.28, ADR 1038): the one whose module setting
/// equals the Host, read before any tenant is known, so outside the tenant filter.</summary>
public static class AnonymousHosts
{
    public static async Task<Guid?> TenantForAsync(AlcifraDbContext dbContext, string moduleId, string settingKey, string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var wanted = host.Trim().ToLowerInvariant();
        var matches = await dbContext.ModuleSettingValues
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.ModuleId == moduleId && v.Key == settingKey && v.Value.ToLower() == wanted)
            .Select(v => v.TenantId)
            .Distinct()
            .Take(2)
            .ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;   // two tenants naming one host is nobody's host
    }
}

/// <summary>A sliding window per client address for anonymous module requests (<c>Modules:AnonymousRateLimit:PerMinute</c>,
/// default 120). One instance for the process; the limiter forgets an idle address on its own.</summary>
public sealed class AnonymousRequestLimiter(IConfiguration configuration) : IDisposable
{
    private readonly int _perMinute = Math.Max(1, configuration.GetValue("Modules:AnonymousRateLimit:PerMinute", 120));
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetSlidingWindowLimiter(address, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, configuration.GetValue("Modules:AnonymousRateLimit:PerMinute", 120)),
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public int PerMinute => _perMinute;

    public bool Admit(string address)
    {
        using var lease = _limiter.AttemptAcquire(address);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
