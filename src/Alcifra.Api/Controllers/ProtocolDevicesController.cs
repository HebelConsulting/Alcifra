using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.ProtocolDevices;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The caller's protocol devices (#1970): each device that reaches mail, files, calendars or contacts with a password of
/// its own, its four switches, and whether its mail is sealed.
/// </summary>
/// <remarks>
/// <para>
/// Reached by <c>me</c>'s <c>protocolDevices</c> rel. One rel per resource, the method says which action (ADR 0719): the
/// collection lists (GET) and enrols (POST, the password is in the answer ONCE); a device reads (GET), changes (PUT, the
/// full value, If-Match) and is revoked (DELETE, If-Match). What the caller may do is a flag on each device, not a rel.
/// </para>
/// <para>
/// The two passwords from before enrolment are listed as devices too, and can only be revoked: they reach what they
/// always reached, and their mail behaves as IMAP always did. User-only: a service account has no devices.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/devices")]
[Authorize]
public class ProtocolDevicesController(
    AlcifraDbContext dbContext,
    ICurrentUserAccessor currentUser,
    ICurrentTenantAccessor currentTenant,
    ProtocolDeviceEnrolment enrolment,
    Concurrency.ProtocolDeviceVerbs devices,
    DeviceHandOvers handOvers,
    IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<Alcifra.Api.Imap.ImapOptions> imap,
    IAuditRecorder audit,
    ILogger<ProtocolDevicesController> logger) : ControllerBase
{
    private const string Collection = "/api/me/devices";

    // Plain mutable classes — XmlSerializer (ADRs 0189/0190).
    public class ProtocolDeviceResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary><c>enrolled</c>, or <c>earlierDav</c> / <c>earlierImap</c> for a password from before enrolment.</summary>
        public string Kind { get; set; } = string.Empty;

        public bool Mail { get; set; }

        public bool Files { get; set; }

        public bool Calendars { get; set; }

        public bool Contacts { get; set; }

        /// <summary>Whether its mail is sealed as things stand: on where the organisation seals mail, whatever was chosen.</summary>
        public bool EncryptMail { get; set; }

        /// <summary>The organisation seals mail, so the setting is shown on and cannot be changed ("Required by your
        /// organisation").</summary>
        public bool EncryptMailLocked { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? LastUsedAt { get; set; }

        /// <summary>Whether its name, switches and encryption may be changed: an enrolled device's, not an earlier password's.</summary>
        public bool CanEdit { get; set; }

        public bool CanDelete { get; set; }

        /// <summary>The precondition of a PUT or DELETE.</summary>
        public string ETag { get; set; } = string.Empty;
    }

    public class EnrolledProtocolDeviceResource : ProtocolDeviceResource
    {
        /// <summary>The device's password — returned ONCE, at enrolment; only its hash is stored.</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>The user name every protocol signs in with.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Everything a person needs to set the device up (#1970 slice 2).</summary>
        public DeviceHandOverResource HandOver { get; set; } = new();
    }

    /// <summary>
    /// The hand-over (#1970 slice 2; owner, 2026-10-29/31): every server address, a QR code of the password alone (for
    /// apps that scan into a field), and, where the device reaches mail, calendars or contacts, a one-time, short-lived
    /// link to a configuration profile with its QR code (for a phone's camera). The answer is a credential while it is
    /// shown, so it is never cached.
    /// </summary>
    public class DeviceHandOverResource
    {
        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public string? ProfileUrl { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public string? ProfileQr { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? ProfileExpiresAt { get; set; }

        /// <summary>The password alone, as a PNG data URL; the user name is shown beside it as text.</summary>
        public string PasswordQr { get; set; } = string.Empty;

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public string? ImapHost { get; set; }

        public int ImapPort { get; set; }

        public bool ImapTls { get; set; }

        /// <summary>The outgoing server a mail client names: the installation's MX on port 25, without a sign-in, until
        /// authenticated submission exists (#2046).</summary>
        public string OutgoingHost { get; set; } = string.Empty;

        public string WebDavUrl { get; set; } = string.Empty;

        public string CalDavUrl { get; set; } = string.Empty;

        public string CardDavUrl { get; set; } = string.Empty;
    }

    public class ProtocolDevicesResource : HypermediaResource
    {
        public List<ProtocolDeviceResource> Items { get; set; } = [];

        public bool EncryptMailLocked { get; set; }
    }

    /// <summary>A device in full (no PATCH, ADR 0872). Every switch is on unless said otherwise, as at enrolment.</summary>
    public class ProtocolDeviceRequest
    {
        public string Name { get; set; } = string.Empty;

        public bool Mail { get; set; } = true;

        public bool Files { get; set; } = true;

        public bool Calendars { get; set; } = true;

        public bool Contacts { get; set; } = true;

        public bool EncryptMail { get; set; }

        public DeviceSwitches Switches() => new(Mail, Files, Calendars, Contacts, EncryptMail);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        var locked = await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken);
        var rows = await dbContext.ProtocolDevices.AsNoTracking()
            .Where(d => d.UserId == user.Id)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

        return Ok(new ProtocolDevicesResource
        {
            Items = [.. rows.Select(d => ToResource<ProtocolDeviceResource>(d, locked))],
            EncryptMailLocked = locked,
            Links = [new Link("self", Collection, "GET")],
        });
    }

    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken) =>
        await CallerAsync(cancellationToken) is null ? Forbid() : NoContent();

    [HttpGet("{deviceId:guid}")]
    public async Task<IActionResult> Get(Guid deviceId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        if (await FindAsync(user.Id, deviceId, cancellationToken) is not { } device)
        {
            return NotFound();
        }

        devices.EmitETag(Response, device);
        return Ok(ToResource<ProtocolDeviceResource>(device, await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken)));
    }

    [HttpHead("{deviceId:guid}")]
    public async Task<IActionResult> Head(Guid deviceId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        if (await FindAsync(user.Id, deviceId, cancellationToken) is not { } device)
        {
            return NotFound();
        }

        devices.EmitETag(Response, device);
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Enrol([FromBody] ProtocolDeviceRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        var locked = await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken);
        var name = await enrolment.NameAsync(user.Id, request.Name, exceptDeviceId: null, cancellationToken);
        await enrolment.RequireSealableAsync(user.Id, request.EncryptMail, locked, cancellationToken);

        var switches = request.Switches();
        var (device, password) = await enrolment.EnrolAsync(user, name, switches, cancellationToken);
        var servers = DeviceServers.From(configuration, imap.Value);
        var profileReaches = (switches.Mail && servers.ImapHost is not null) || switches.Calendars || switches.Contacts;
        var link = profileReaches
            ? handOvers.Create(device, DeviceProfile.Build(device.Id, device.Name, user.Email, password, switches, servers))
            : ((string Token, DateTimeOffset ExpiresAt)?)null;
        await dbContext.SaveChangesAsync(cancellationToken);

        // A long-lived credential that bypasses the interactive login and its MFA, as the earlier ones did (#1092): the
        // event says one was issued, to whom and for what; the password never is.
        logger.LogInformation("Protocol device {DeviceId} enrolled for user {UserId}", device.Id, user.Id);
        await audit.RecordAsync(AuditActions.ProtocolDeviceEnrolled, "User", user.Id, device.Name,
            $"Enrolled the device '{device.Name}' ({Protocols(device)}).", cancellationToken: cancellationToken);

        var resource = ToResource<EnrolledProtocolDeviceResource>(device, locked);
        resource.Password = password;
        resource.Username = user.Email;
        var profileUrl = link is { } l ? $"{servers.WebOrigin}/api/device-profiles/{l.Token}" : null;
        resource.HandOver = new DeviceHandOverResource
        {
            ProfileUrl = profileUrl,
            ProfileQr = profileUrl is null ? null : Alcifra.Api.Security.QrCodes.PngDataUrl(profileUrl),
            ProfileExpiresAt = link?.ExpiresAt,
            PasswordQr = Alcifra.Api.Security.QrCodes.PngDataUrl(password),
            ImapHost = servers.ImapHost,
            ImapPort = servers.ImapPort,
            ImapTls = servers.ImapTls,
            OutgoingHost = servers.OutgoingHost,
            WebDavUrl = $"{servers.WebOrigin}{Alcifra.Api.WebDav.WebDavMiddleware.BasePath}",
            CalDavUrl = $"{servers.WebOrigin}{Alcifra.Api.CalDav.DavProtocol.CalDav.BasePath}",
            CardDavUrl = $"{servers.WebOrigin}{Alcifra.Api.CalDav.DavProtocol.CardDav.BasePath}",
        };
        Response.Headers.CacheControl = "no-store";
        devices.EmitETag(Response, device);
        return Created($"{Collection}/{device.Id}", resource);
    }

    [HttpPut("{deviceId:guid}")]
    public async Task<IActionResult> Update(Guid deviceId, [FromBody] ProtocolDeviceRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        if (await FindAsync(user.Id, deviceId, cancellationToken) is not { } device)
        {
            return NotFound();
        }

        if (device.Kind != ProtocolDeviceKind.Enrolled)
        {
            throw new ProtocolDeviceNotEditableException();
        }

        devices.RequireIfMatch(Request);
        var locked = await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken);
        var name = await enrolment.NameAsync(user.Id, request.Name, device.Id, cancellationToken);
        await enrolment.RequireSealableAsync(user.Id, request.EncryptMail, locked, cancellationToken);

        await devices.MutateAsync(Request, device,
            apply: () =>
            {
                device.Name = name;
                request.Switches().ApplyTo(device);
                return Task.CompletedTask;
            },
            afterCommit: () => audit.RecordAsync(AuditActions.ProtocolDeviceChanged, "User", user.Id, device.Name,
                $"Changed the device '{device.Name}' ({Protocols(device)}).", cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);

        devices.EmitETag(Response, device);
        return Ok(ToResource<ProtocolDeviceResource>(device, locked));
    }

    [HttpDelete("{deviceId:guid}")]
    public async Task<IActionResult> Revoke(Guid deviceId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        if (await FindAsync(user.Id, deviceId, cancellationToken) is not { } device)
        {
            return NotFound();
        }

        devices.RequireIfMatch(Request);
        await devices.MutateAsync(Request, device,
            apply: () =>
            {
                dbContext.ProtocolDevices.Remove(device);
                return Task.CompletedTask;
            },
            afterCommit: async () =>
            {
                // Information: whatever signed in with this password stops at its next request.
                logger.LogInformation("Protocol device {DeviceId} of user {UserId} revoked", device.Id, user.Id);
                await audit.RecordAsync(AuditActions.ProtocolDeviceRevoked, "User", user.Id, device.Name,
                    $"Revoked the device '{device.Name}'.", cancellationToken: cancellationToken);
            },
            touchEntity: false,
            cancellationToken: cancellationToken);

        return NoContent();
    }

    /// <summary>The signed-in USER, or null for a service account or platform administrator (no devices).</summary>
    private async Task<User?> CallerAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId && currentTenant.TenantId is not null
            ? await dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            : null;

    private Task<ProtocolDevice?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        dbContext.ProtocolDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, cancellationToken);

    private static string Protocols(ProtocolDevice device) =>
        string.Join(", ", new[]
        {
            device.MailEnabled ? "mail" : null,
            device.FilesEnabled ? "files" : null,
            device.CalendarsEnabled ? "calendars" : null,
            device.ContactsEnabled ? "contacts" : null,
        }.Where(p => p is not null)) is { Length: > 0 } list ? list : "no protocols";

    private static T ToResource<T>(ProtocolDevice device, bool locked) where T : ProtocolDeviceResource, new() => new()
    {
        Id = device.Id,
        Name = device.Name,
        Kind = device.Kind switch
        {
            ProtocolDeviceKind.LegacyDav => "earlierDav",
            ProtocolDeviceKind.LegacyImap => "earlierImap",
            _ => "enrolled",
        },
        Mail = device.MailEnabled,
        Files = device.FilesEnabled,
        Calendars = device.CalendarsEnabled,
        Contacts = device.ContactsEnabled,
        EncryptMail = locked || (device.Kind == ProtocolDeviceKind.Enrolled && device.EncryptMail),
        EncryptMailLocked = locked,
        CreatedAt = device.CreatedAt,
        LastUsedAt = device.LastUsedAt,
        CanEdit = device.Kind == ProtocolDeviceKind.Enrolled,
        CanDelete = true,
        ETag = $"\"{device.ConcurrencyToken}\"",
        Links = [new Link("self", $"{Collection}/{device.Id}", "GET")],
    };
}
