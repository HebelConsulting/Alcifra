using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// The two passwords from before per-device enrolment, as devices (#1970): what each is called and what it opens,
/// defined once for the migration's rows, the earlier endpoints and the demo seed.
/// </summary>
/// <remarks>
/// Until slice 3 retires them (owner, 2026-10-31), the earlier "WebDAV access" and "IMAP access" endpoints set and
/// clear these rows instead of the user's columns, so every current client keeps working unchanged.
/// </remarks>
public static class LegacyProtocolDevices
{
    public const string DavName = "Earlier WebDAV/calendar password";

    public const string ImapName = "Earlier IMAP password";

    /// <summary>A new legacy device of <paramref name="kind"/> with the protocols that password always reached.</summary>
    public static ProtocolDevice New(User user, ProtocolDeviceKind kind, string secretHash, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = user.TenantId,
        UserId = user.Id,
        Kind = kind,
        Name = kind == ProtocolDeviceKind.LegacyImap ? ImapName : DavName,
        SecretHash = secretHash,
        MailEnabled = kind == ProtocolDeviceKind.LegacyImap,
        FilesEnabled = kind == ProtocolDeviceKind.LegacyDav,
        CalendarsEnabled = kind == ProtocolDeviceKind.LegacyDav,
        ContactsEnabled = kind == ProtocolDeviceKind.LegacyDav,
        CreatedAt = now,
    };

    /// <summary>Whether the user has the legacy device of this kind.</summary>
    public static Task<bool> ExistsAsync(AlcifraDbContext db, Guid userId, ProtocolDeviceKind kind, CancellationToken cancellationToken) =>
        db.ProtocolDevices.AnyAsync(d => d.UserId == userId && d.Kind == kind, cancellationToken);

    /// <summary>Sets the legacy device's password, creating the device the first time. Tracked only; the caller saves.</summary>
    public static async Task SetAsync(
        AlcifraDbContext db, User user, ProtocolDeviceKind kind, string secretHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.ProtocolDevices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.Kind == kind, cancellationToken) is { } existing)
        {
            existing.SecretHash = secretHash;
        }
        else
        {
            db.ProtocolDevices.Add(New(user, kind, secretHash, now));
        }
    }

    /// <summary>Removes the legacy device of this kind. Tracked only; the caller saves.</summary>
    public static async Task RemoveAsync(AlcifraDbContext db, Guid userId, ProtocolDeviceKind kind, CancellationToken cancellationToken) =>
        db.ProtocolDevices.RemoveRange(
            await db.ProtocolDevices.Where(d => d.UserId == userId && d.Kind == kind).ToListAsync(cancellationToken));
}
