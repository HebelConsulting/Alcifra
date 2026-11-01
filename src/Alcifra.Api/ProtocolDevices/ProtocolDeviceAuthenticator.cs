using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>The four protocols a device's switches open (#1970).</summary>
public enum DeviceProtocol
{
    Mail,
    Files,
    Calendars,
    Contacts,
}

/// <summary>A device login that succeeded: who, and through which device.</summary>
public sealed record DeviceSignIn(User User, ProtocolDevice Device);

/// <summary>
/// The ONE check of a protocol password, for IMAP, WebDAV, CalDAV and CardDAV alike (#1970).
/// </summary>
/// <remarks>
/// <para>
/// <b>A wrong password, an unknown address and a switch that is off all answer the same: no.</b> A prober learns
/// nothing from the difference. The switch is the one case the user cannot see the reason for, so it is logged at
/// Warning naming the device and the switch (ADR 0626); the password never is.
/// </para>
/// <para>
/// No tenant is known before the user is found, so the lookups bypass the tenant filter, as every pre-tenant login
/// lookup does (CLAUDE.md). The user is found by <c>NormalizedEmail</c>: an identity alias never signs in (ADR 1015).
/// </para>
/// </remarks>
public sealed class ProtocolDeviceAuthenticator(
    AlcifraDbContext dbContext,
    TimeProvider clock,
    ILogger<ProtocolDeviceAuthenticator> logger)
{
    // Constructed, not injected, as every protocol door did before: the hasher is stateless and not registered.
    private readonly PasswordHasher<User> passwordHasher = new();

    /// <summary>How often a login may move <see cref="ProtocolDevice.LastUsedAt"/>: often enough to read, rarely enough
    /// not to write on every DAV request.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromHours(1);

    private static readonly string[] TenantFilter = ["TenantFilter"];

    /// <summary>The user and device this password opens <paramref name="protocol"/> for, or null.</summary>
    public async Task<DeviceSignIn?> SignInAsync(
        string email, string password, DeviceProtocol protocol, CancellationToken cancellationToken = default)
    {
        var normalized = email.ToUpperInvariant();
        var user = await dbContext.Users.IgnoreQueryFilters(TenantFilter)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var device = await DeviceOpenedByAsync(user, password, protocol, cancellationToken);
        if (device is null)
        {
            return null;
        }

        if (!Opens(device, protocol))
        {
            logger.LogWarning(
                "Device {DeviceId} of user {UserId} presented a valid password for {Protocol}, which its switch turns off; refused as a wrong password. Trace carries the exchange.",
                device.Id, user.Id, protocol);
            return null;
        }

        await TouchAsync(device, cancellationToken);
        return new DeviceSignIn(user, device);
    }

    /// <summary>Whether a device's switches open a protocol.</summary>
    public static bool Opens(ProtocolDevice device, DeviceProtocol protocol) => protocol switch
    {
        DeviceProtocol.Mail => device.MailEnabled,
        DeviceProtocol.Files => device.FilesEnabled,
        DeviceProtocol.Calendars => device.CalendarsEnabled,
        DeviceProtocol.Contacts => device.ContactsEnabled,
        _ => false,
    };

    // With a key, exactly one hash is verified: the enrolled device the key names, if it is this user's. Without one,
    // the password is a legacy one and is tried against the user's legacy devices (at most two).
    private async Task<ProtocolDevice?> DeviceOpenedByAsync(User user, string password, DeviceProtocol protocol, CancellationToken cancellationToken)
    {
        var devices = dbContext.ProtocolDevices.IgnoreQueryFilters(TenantFilter).AsNoTracking().Where(d => d.UserId == user.Id);

        if (ProtocolDevicePassword.TryParse(password, out var key, out var secret))
        {
            var enrolled = await devices
                .FirstOrDefaultAsync(d => d.Kind == ProtocolDeviceKind.Enrolled && d.CredentialKey == key, cancellationToken);
            return enrolled is not null && Verifies(user, enrolled.SecretHash, secret) ? enrolled : null;
        }

        // Both earlier passwords may be the SAME (the demo seed gives every demo login one password for everything), so
        // the device that opens this protocol is preferred; one that only matches is the "switch is off" case, reported
        // by the caller. Taking the first match alone sent a WebDAV login to the IMAP device and refused it.
        var legacy = await devices.Where(d => d.Kind != ProtocolDeviceKind.Enrolled).ToListAsync(cancellationToken);
        var matching = legacy.Where(d => Verifies(user, d.SecretHash, password)).ToList();
        return matching.FirstOrDefault(d => Opens(d, protocol)) ?? matching.FirstOrDefault();
    }

    private bool Verifies(User user, string hash, string secret) =>
        passwordHasher.VerifyHashedPassword(user, hash, secret) != PasswordVerificationResult.Failed;

    // A CLAIM, TOKEN DELIBERATELY NOT MOVED: last-used drifts on its own, and moving the token would make every edit of
    // the device a 412 after the device merely logged in (ADR 0878). At most once per LastUsedResolution.
    private async Task TouchAsync(ProtocolDevice device, CancellationToken cancellationToken)
    {
        // Decided on the value just read, in memory (SQLite cannot translate a DateTimeOffset comparison), and written only
        // if that value is still the one read: of two instances logging the same device in, one moves it.
        var now = clock.GetUtcNow();
        if (device.LastUsedAt is { } used && used >= now - LastUsedResolution)
        {
            return;
        }

        var read = device.LastUsedAt;
        await dbContext.ProtocolDevices.IgnoreQueryFilters(TenantFilter)
            .Where(d => d.Id == device.Id && d.LastUsedAt == read)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.LastUsedAt, now), cancellationToken);
    }
}
