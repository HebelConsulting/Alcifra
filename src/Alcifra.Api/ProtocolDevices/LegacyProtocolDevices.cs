using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// The two passwords from before per-device enrolment, as devices (#1970): what each is called and what it opens,
/// defined once for the migration's rows, the earlier endpoints and the demo seed.
/// </summary>
/// <remarks>
/// The earlier "WebDAV access" and "IMAP access" endpoints that set these rows are retired (slice 3): an earlier password
/// keeps working until it is revoked, and a new one is a device.
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
}
