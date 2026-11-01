using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// The one-time, short-lived link to a new device's profile (#1970 slice 2; owner, 2026-10-29/31): made at enrolment,
/// taken once by whoever holds the link, gone after about ten minutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is the authorisation.</b> A new phone's camera opens the link in a browser that is not signed in, so
/// the link's 256-bit token is what grants the profile. Only its SHA-256 is stored; the token is in the link and its QR
/// code and nowhere else.
/// </para>
/// <para>
/// <b>Taken by the delete that removes it.</b> Reading then deleting would let two openers both read it; here the row is
/// read and then deleted by its id, and only the opener whose delete removed it gets the profile. Both app instances
/// share the data-protection keys (they are kept in the database), so either instance can unseal it.
/// </para>
/// </remarks>
public sealed class DeviceHandOvers(AlcifraDbContext dbContext, IDataProtectionProvider dataProtection, TimeProvider clock)
{
    /// <summary>How long a hand-over link works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

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

    /// <summary>The profile and its device, ONCE: null when the link is unknown, expired, or was taken by someone else.</summary>
    public async Task<(string Profile, ProtocolDevice Device)?> TakeAsync(string token, CancellationToken cancellationToken)
    {
        var handOver = await LiveAsync(token, cancellationToken);
        if (handOver is null)
        {
            return null;
        }

        var removed = await dbContext.ProtocolDeviceHandOvers.IgnoreQueryFilters(TenantFilter)
            .Where(h => h.Id == handOver.Id)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed != 1)
        {
            return null;
        }

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
        return handOver is not null && handOver.ExpiresAt > clock.GetUtcNow() ? handOver : null;
    }

    private static string Base64UrlToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
