using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// The short-lived link to a new device's profile (#1970 slice 2; owner, 2026-10-29/31): made at enrolment, taken by
/// whoever holds the link, gone after about ten minutes, or two minutes after its first take (ADR 1028).
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is the authorisation.</b> A new phone's camera opens the link in a browser that is not signed in, so
/// the link's 256-bit token is what grants the profile. Only its SHA-256 is stored; the token is in the link and its QR
/// code and nowhere else.
/// </para>
/// <para>
/// <b>Taken by a claim.</b> A browser other than the phone's installer (another browser on an iPhone) cannot install
/// a profile: it downloads it, then hands it to the one that can, which fetches the link AGAIN. So the first take
/// leaves the link working for <see cref="Grace"/>, at most <see cref="MaxTakes"/> times in all (owner, 2026-11-01,
/// ADR 1028). Each take is a compare-and-swap on the count it read, and the last one deletes the row, so two openers
/// never get the same take. Both app instances share the data-protection keys (they are kept in the database), so
/// either instance can unseal it.
/// </para>
/// </remarks>
public sealed class DeviceHandOvers(AlcifraDbContext dbContext, IDataProtectionProvider dataProtection, TimeProvider clock)
{
    /// <summary>How long a hand-over link works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long the link keeps working after its first take, never past its <see cref="Lifetime"/>.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>How often the link hands out the profile in all.</summary>
    public const int MaxTakes = 3;

    private static readonly string[] TenantFilter = ["TenantFilter"];

    private IDataProtector Protector => dataProtection.CreateProtector("Alcifra.ProtocolDevices.HandOver.v1");

    /// <summary>A new hand-over of <paramref name="profile"/> and the token for its link. Tracked only; the caller saves.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Create(ProtocolDevice device, string profile)
    {
        var token = Base64UrlToken();
        var expiresAt = clock.GetUtcNow() + Lifetime;
        dbContext.ProtocolDeviceHandOvers.Add(new ProtocolDeviceHandOver
        {
            Id = Guid.NewGuid(),
            TenantId = device.TenantId,
            DeviceId = device.Id,
            TokenHash = Hash(token),
            SealedProfile = Protector.Protect(Encoding.UTF8.GetBytes(profile)),
            ExpiresAt = expiresAt,
        });
        return (token, expiresAt);
    }

    /// <summary>Whether the link still works, without taking it (a HEAD).</summary>
    public async Task<bool> ExistsAsync(string token, CancellationToken cancellationToken) =>
        await LiveAsync(token, cancellationToken) is not null;

    /// <summary>The profile and its device: null when the link is unknown, expired, or taken <see cref="MaxTakes"/> times.</summary>
    public async Task<(string Profile, ProtocolDevice Device)?> TakeAsync(string token, CancellationToken cancellationToken)
    {
        // A claim lost to a concurrent take reads the row again; every attempt that loses means another take succeeded,
        // so the count rises each round and the loop ends within MaxTakes rounds.
        for (var attempt = 0; attempt < MaxTakes; attempt++)
        {
            var handOver = await LiveAsync(token, cancellationToken);
            if (handOver is null)
            {
                return null;
            }

            if (await ClaimAsync(handOver, cancellationToken))
            {
                return await OpenAsync(handOver, cancellationToken);
            }
        }

        return null;
    }

    // One take, compare-and-swapped on the count read: the last take deletes the row; an earlier one counts it and pulls
    // the expiry in to the grace (computed here, since SQLite cannot compare DateTimeOffsets in SQL).
    private async Task<bool> ClaimAsync(ProtocolDeviceHandOver handOver, CancellationToken cancellationToken)
    {
        var rows = dbContext.ProtocolDeviceHandOvers.IgnoreQueryFilters(TenantFilter)
            .Where(h => h.Id == handOver.Id && h.TakenCount == handOver.TakenCount);
        if (handOver.TakenCount + 1 >= MaxTakes)
        {
            return await rows.ExecuteDeleteAsync(cancellationToken) == 1;
        }

        var graceEnds = clock.GetUtcNow() + Grace;
        var expiresAt = handOver.ExpiresAt < graceEnds ? handOver.ExpiresAt : graceEnds;
        return await rows.ExecuteUpdateAsync(s => s
            .SetProperty(h => h.TakenCount, handOver.TakenCount + 1)
            .SetProperty(h => h.ExpiresAt, expiresAt), cancellationToken) == 1;
    }

    private async Task<(string Profile, ProtocolDevice Device)> OpenAsync(ProtocolDeviceHandOver handOver, CancellationToken cancellationToken)
    {
        // The profile sets up an iPhone or iPad, which offers no way to pick collections: the device now shows the chosen
        // ones (#2090, owner 2026-11-02). It changes what the device's editor shows, so the token moves; idempotent, so a
        // second take changes nothing.
        await dbContext.ProtocolDevices.IgnoreQueryFilters(TenantFilter)
            .Where(d => d.Id == handOver.DeviceId && !d.CollectionsChosen)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.CollectionsChosen, true)
                .SetProperty(d => d.ConcurrencyToken, Guid.NewGuid()), cancellationToken);

        var device = await dbContext.ProtocolDevices.IgnoreQueryFilters(TenantFilter).AsNoTracking()
            .FirstAsync(d => d.Id == handOver.DeviceId, cancellationToken);
        return (Encoding.UTF8.GetString(Protector.Unprotect(handOver.SealedProfile)), device);
    }

    /// <summary>Removes expired hand-overs; idempotent, so both instances may run it.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        // The expiry is compared in memory, as the reminder and escalation sweeps do: SQLite cannot translate a
        // DateTimeOffset comparison, and there are only ever a handful of rows.
        var now = clock.GetUtcNow();
        var expired = (await dbContext.ProtocolDeviceHandOvers.IgnoreQueryFilters(TenantFilter).AsNoTracking()
                .Select(h => new { h.Id, h.ExpiresAt }).ToListAsync(cancellationToken))
            .Where(h => h.ExpiresAt <= now).Select(h => h.Id).ToList();
        return expired.Count == 0
            ? 0
            : await dbContext.ProtocolDeviceHandOvers.IgnoreQueryFilters(TenantFilter)
                .Where(h => expired.Contains(h.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    // The row the token names while it still works. Found by the token's hash (unique); its expiry compared in memory.
    private async Task<ProtocolDeviceHandOver?> LiveAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var handOver = await dbContext.ProtocolDeviceHandOvers.IgnoreQueryFilters(TenantFilter).AsNoTracking()
            .FirstOrDefaultAsync(h => h.TokenHash == hash, cancellationToken);
        return handOver is not null && handOver.ExpiresAt > clock.GetUtcNow() && handOver.TakenCount < MaxTakes ? handOver : null;
    }

    private static string Base64UrlToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
