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
    Concurrency.UserVerbs users,
    DeviceHandOvers handOvers,
    DeviceCollections collections,
    Alcifra.Api.Documents.PersonalMailboxProvisioner mailbox,
    IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<Alcifra.Api.Imap.ImapOptions> imap,
    Alcifra.Api.Imap.ImapServer imapServer,
    Alcifra.Api.Encryption.TenantIngestKeyService ingestKeys,
    IAuditRecorder audit,
    ILogger<ProtocolDevicesController> logger,
    Alcifra.Infrastructure.Modules.ModuleDeviceProfiles moduleProfiles) : ControllerBase
{
    private const string Collection = "/api/me/devices";

    // Plain mutable classes — XmlSerializer (ADRs 0189/0190).
    public class ProtocolDeviceResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary><c>enrolled</c>; <c>earlierDav</c> / <c>earlierImap</c> for a password from before enrolment; <c>seeded</c>
        /// for the demo seed's device (ADR 1030).</summary>
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

        /// <summary>
        /// Whether the device shows CHOSEN calendars and address books (#2090): the user's own always, a shared one only
        /// when it is in <see cref="ChosenCollections"/>. False: all of them.
        /// </summary>
        public bool CollectionsChosen { get; set; }

        /// <summary>The shared collections ticked on the device (folder ids); the <c>collections</c> rel names them.</summary>
        public List<Guid> ChosenCollections { get; set; } = [];

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

        /// <summary>Where the device reaches the installation.</summary>
        public DeviceServersResource Servers { get; set; } = new();
    }

    /// <summary>Every address a device is set up with, and the user name all of them take (#1970).</summary>
    public class DeviceServersResource
    {
        public string Username { get; set; } = string.Empty;

        /// <summary>Whether this installation serves IMAP at all.</summary>
        public bool ImapAvailable { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public string? ImapHost { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public int? ImapPort { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public int? ImapTlsPort { get; set; }

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

        /// <summary>The user's own mail view, for every device (#562; owner, 2026-11-01): false shows emails only, true every
        /// visible document. Changed through the <c>settings</c> rel.</summary>
        public bool ShowAllDocuments { get; set; }

        public DeviceServersResource Servers { get; set; } = new();
    }

    /// <summary>The user's device settings, in full (no PATCH, ADR 0872).</summary>
    public class DeviceSettingsRequest
    {
        public bool ShowAllDocuments { get; set; }
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

        /// <summary>
        /// All or chosen calendars and address books (#2090). Null leaves the stored setting as it is: a client from before
        /// the setting existed sends none, and its save must not switch an iPhone back to everything.
        /// </summary>
        public bool? CollectionsChosen { get; set; }

        /// <summary>The ticked shared collections, in full; null leaves them as they are, for the same reason.</summary>
        public List<Guid>? ChosenCollections { get; set; }

        public DeviceSwitches Switches() => new(Mail, Files, Calendars, Contacts, EncryptMail);
    }

    /// <summary>The shared calendars and address books a device can tick (#2090).</summary>
    public class DeviceCollectionsResource : HypermediaResource
    {
        public List<DeviceCollectionResource> Items { get; set; } = [];
    }

    public class DeviceCollectionResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary><c>calendar</c> or <c>contacts</c>.</summary>
        public string Kind { get; set; } = string.Empty;

        public bool Ticked { get; set; }
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
        var ticked = await collections.TickedAsync([.. rows.Select(d => d.Id)], cancellationToken);

        return Ok(new ProtocolDevicesResource
        {
            Items = [.. rows.Select(d => ToResource<ProtocolDeviceResource>(d, locked, ticked.GetValueOrDefault(d.Id)))],
            EncryptMailLocked = locked,
            ShowAllDocuments = user.ImapShowAllDocuments,
            Servers = await ServersOfAsync(user, cancellationToken),
            Links = [new Link("self", Collection, "GET"), new Link("settings", $"{Collection}/settings", "PUT")],
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
        return Ok(ToResource<ProtocolDeviceResource>(device, await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken),
            (await collections.TickedAsync([device.Id], cancellationToken)).GetValueOrDefault(device.Id)));
    }

    /// <summary>The shared calendars and address books the device can tick (#2090); the user's own are always shown.</summary>
    [HttpGet("{deviceId:guid}/collections")]
    public async Task<IActionResult> Collections(Guid deviceId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        if (await FindAsync(user.Id, deviceId, cancellationToken) is not { } device)
        {
            return NotFound();
        }

        return Ok(new DeviceCollectionsResource
        {
            Items = [.. (await collections.ChoicesAsync(user.Id, device.Id, cancellationToken))
                .Select(c => new DeviceCollectionResource { Id = c.Id, Name = c.Name, Kind = c.Kind, Ticked = c.Ticked })],
            Links = [new Link("self", $"{Collection}/{device.Id}/collections", "GET")],
        });
    }

    [HttpHead("{deviceId:guid}/collections")]
    public async Task<IActionResult> HeadCollections(Guid deviceId, CancellationToken cancellationToken) =>
        await CallerAsync(cancellationToken) is not { } user ? Forbid()
        : await FindAsync(user.Id, deviceId, cancellationToken) is null ? NotFound()
        : Ok();

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
        var servers = DeviceServers.From(configuration, imap.Value, imapServer);
        var profileReaches = (switches.Mail && servers.ImapHost is not null) || switches.Calendars || switches.Contacts;

        // A device that reaches mail carries the tenant's mail-in certificate (ADR 0820), minted on first need as the
        // retired self-service identity minted it (#1970 slice 4). Null without a verified receiving domain.
        var ingest = switches.Mail ? await ingestKeys.EnsureAsync(user.TenantId, cancellationToken) : null;
        // What the active modules add (ABI 1.27, ADR 1036): a device whose profile would otherwise carry nothing still
        // gets one when a module has a part for it.
        var extra = await moduleProfiles.PayloadsAsync(user.TenantId, user.Id, user.Email, device.Id, device.Name, servers.WebHost, cancellationToken);
        var link = profileReaches || extra.Count > 0
            ? handOvers.Create(device, DeviceProfile.Build(device.Id, device.Name, user.Email, password, switches, servers, ingest?.CertificatePem, extra))
            : ((string Token, DateTimeOffset ExpiresAt)?)null;
        await dbContext.SaveChangesAsync(cancellationToken);

        // A long-lived credential that bypasses the interactive login and its MFA, as the earlier ones did (#1092): the
        // event says one was issued, to whom and for what; the password never is.
        logger.LogInformation("Protocol device {DeviceId} enrolled for user {UserId}", device.Id, user.Id);

        // A device that reaches mail is a statement of intent to use the mailbox (#562): create it now, so a freshly set
        // up mail client has something to subscribe to rather than waiting for the first delivered message.
        if (switches.Mail)
        {
            await mailbox.EnsureMailboxAsync(user.TenantId, user.Id, cancellationToken);
        }
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
            Servers = await ServersOfAsync(user, cancellationToken),
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

        if (LegacyProtocolDevices.IsEarlier(device.Kind))
        {
            throw new ProtocolDeviceNotEditableException();
        }

        devices.RequireIfMatch(Request);
        var locked = await enrolment.OrganisationSealsMailAsync(user.TenantId, cancellationToken);
        var name = await enrolment.NameAsync(user.Id, request.Name, device.Id, cancellationToken);
        await enrolment.RequireSealableAsync(user.Id, request.EncryptMail, locked, cancellationToken);

        await devices.MutateAsync(Request, device,
            apply: async () =>
            {
                device.Name = name;
                request.Switches().ApplyTo(device);
                if (request.CollectionsChosen is { } chosen)
                {
                    device.CollectionsChosen = chosen;
                }

                if (request.ChosenCollections is { } ticks)
                {
                    await collections.ReplaceAsync(device, ticks, cancellationToken);
                }
            },
            afterCommit: () => audit.RecordAsync(AuditActions.ProtocolDeviceChanged, "User", user.Id, device.Name,
                $"Changed the device '{device.Name}' ({Protocols(device)}).", cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);

        devices.EmitETag(Response, device);
        return Ok(ToResource<ProtocolDeviceResource>(device, locked,
            (await collections.TickedAsync([device.Id], cancellationToken)).GetValueOrDefault(device.Id)));
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
                // The modules revoke what they issued for the device (ABI 1.27, ADR 1036); a module's failure is its own.
                await moduleProfiles.DeviceRevokedAsync(user.TenantId, user.Id, device.Id, cancellationToken);
            },
            touchEntity: false,
            cancellationToken: cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// The user's device settings: whether mail shows every visible document or emails only (#562), one setting for all
    /// of the user's devices. A plain boolean me-setting, so tolerant of an absent If-Match, as it always was.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] DeviceSettingsRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } user)
        {
            return Forbid();
        }

        user.ImapShowAllDocuments = request.ShowAllDocuments;
        await users.MutateAsync(Request, user, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);
        return NoContent();
    }

    private async Task<DeviceServersResource> ServersOfAsync(User user, CancellationToken cancellationToken)
    {
        var servers = DeviceServers.From(configuration, imap.Value, imapServer);
        var tenantName = await dbContext.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        var webDav = Alcifra.Api.WebDav.WebDavMiddleware.AccountBase(user.TenantId, user.Id,
            Alcifra.Api.WebDav.WebDavMiddleware.VolumeLabel(user.DisplayName, tenantName));
        return new DeviceServersResource
        {
            Username = user.Email,
            ImapAvailable = servers.ImapHost is not null,
            ImapHost = servers.ImapHost,
            ImapPort = servers.ImapPort,
            ImapTlsPort = servers.ImapTlsPort,
            OutgoingHost = servers.OutgoingHost,
            // The account's own address (#2128, ADR 1043): two accounts on one computer are two volumes. Plain /webdav
            // stays served for good, for addresses typed by hand.
            WebDavUrl = $"{servers.WebOrigin}{webDav}",
            CalDavUrl = $"{servers.WebOrigin}{Alcifra.Api.CalDav.DavProtocol.CalDav.BasePath}",
            CardDavUrl = $"{servers.WebOrigin}{Alcifra.Api.CalDav.DavProtocol.CardDav.BasePath}",
        };
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

    private static T ToResource<T>(ProtocolDevice device, bool locked, List<Guid>? ticked = null) where T : ProtocolDeviceResource, new() => new()
    {
        Id = device.Id,
        Name = device.Name,
        Kind = device.Kind switch
        {
            ProtocolDeviceKind.LegacyDav => "earlierDav",
            ProtocolDeviceKind.LegacyImap => "earlierImap",
            ProtocolDeviceKind.Seeded => "seeded",   // the demo's device: a device like any, whose name carries the demo password
            _ => "enrolled",
        },
        Mail = device.MailEnabled,
        Files = device.FilesEnabled,
        Calendars = device.CalendarsEnabled,
        Contacts = device.ContactsEnabled,
        EncryptMail = locked || (!LegacyProtocolDevices.IsEarlier(device.Kind) && device.EncryptMail),
        EncryptMailLocked = locked,
        CollectionsChosen = device.CollectionsChosen,
        ChosenCollections = ticked ?? [],
        CreatedAt = device.CreatedAt,
        LastUsedAt = device.LastUsedAt,
        CanEdit = !LegacyProtocolDevices.IsEarlier(device.Kind),
        CanDelete = true,
        ETag = $"\"{device.ConcurrencyToken}\"",
        // The shared collections a device can tick, where it can be edited and reaches calendars or contacts (#2090).
        Links = LegacyProtocolDevices.IsEarlier(device.Kind) || !(device.CalendarsEnabled || device.ContactsEnabled)
            ? [new Link("self", $"{Collection}/{device.Id}", "GET")]
            : [new Link("self", $"{Collection}/{device.Id}", "GET"), new Link("collections", $"{Collection}/{device.Id}/collections", "GET")],
    };
}
