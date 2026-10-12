using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimplArchive.Api.CalDav;
using SimplArchive.Api.Concurrency;
using SimplArchive.Api.Errors.Exceptions.PushRegistrations;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A user's phones, registered for push (SimplArchiveMobile #20, slice B; owner decisions 2026-10-12). A device posts
/// its UnifiedPush endpoint and Web Push keys after signing in, refreshes them with <c>If-Match</c>, and removes its
/// registration on sign-out. The collection also tells the phone what it needs to set up: the server's public Web
/// Push key and the address of the installation's push server, for the distributor app.
/// </summary>
/// <remarks>
/// Only a signed-in USER registers a phone; a row is visible and changeable only to its owner. The registration is
/// concurrency-tracked (owner: one user may have several clients), so PUT and DELETE require the tag of the row the
/// device read (428 without, 412 when stale; ADR 0878).
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/push-registrations")]
public sealed class PushRegistrationsController : ControllerBase
{
    private const int EndpointMax = 2000, KeyMax = 200, LabelMax = 100;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ICurrentTenantAccessor _currentTenant;
    private readonly PhonePushRegistrationVerbs _registrations;
    private readonly DavPushConfiguration _webPush;
    private readonly PhonePushOptions _options;
    private readonly IAuditRecorder _audit;
    private readonly bool _development;

    public PushRegistrationsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUser,
        ICurrentTenantAccessor currentTenant,
        PhonePushRegistrationVerbs registrations,
        DavPushConfiguration webPush,
        IOptions<PhonePushOptions> options,
        IAuditRecorder audit,
        IHostEnvironment environment)
    {
        _development = environment.IsDevelopment();
        (_dbContext, _currentUser, _currentTenant, _registrations, _webPush, _audit) = (dbContext, currentUser, currentTenant, registrations, webPush, audit);
        _options = options.Value;
    }

    public sealed class RegistrationRequest
    {
        public string App { get; set; } = string.Empty;
        public string DeviceLabel { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string P256dh { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
    }

    public sealed class RegistrationResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string App { get; set; } = string.Empty;
        public string DeviceLabel { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastRegisteredAt { get; set; }
    }

    public sealed class RegistrationsResource : HypermediaResource
    {
        public List<RegistrationResource> Registrations { get; set; } = [];

        /// <summary>The server's public Web Push (VAPID) key, which the distributor needs; null when push is off.</summary>
        public string? VapidPublicKey { get; set; }

        /// <summary>The installation's push server, for the distributor app's settings; null when none is set.</summary>
        public string? PushServer { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return Forbid();
        }

        var rows = await _dbContext.PhonePushRegistrations.Where(r => r.UserId == userId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        return Ok(new RegistrationsResource
        {
            Registrations = [.. rows.Select(Resource)],
            VapidPublicKey = _webPush.IsEnabled ? _webPush.VapidPublicKey : null,
            PushServer = string.IsNullOrWhiteSpace(_options.ServerUrl) ? null : _options.ServerUrl,
            // One rel for the collection: GET lists, POST to the same address registers (ADR 0719).
            Links = [new Link("self", Url.Action(nameof(List))!, "GET")],
        });
    }

    [HttpHead]
    public IActionResult ListHead() => _currentUser.UserId is null ? Forbid() : NoContent();

    /// <summary>
    /// Registers this device. A device registering again with an address it already registered gets its row back
    /// unchanged, with its tag, and refreshes it with PUT; an address registered to ANOTHER user is refused, or
    /// anyone who learned a device's address could aim their own notifications at it.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegistrationRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId || _currentTenant.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        Validate(request, requireApp: true, _development);
        if (await _dbContext.PhonePushRegistrations.FirstOrDefaultAsync(r => r.Endpoint == request.Endpoint, cancellationToken) is { } existing)
        {
            if (existing.UserId != userId)
            {
                throw new PushEndpointTakenException();
            }

            _registrations.EmitETag(Response, existing);
            return Ok(Resource(existing));
        }

        var now = DateTimeOffset.UtcNow;
        var registration = new PhonePushRegistration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            App = request.App.Trim(),
            DeviceLabel = request.DeviceLabel.Trim(),
            Endpoint = request.Endpoint,
            P256dh = request.P256dh,
            Auth = request.Auth,
            CreatedAt = now,
            LastRegisteredAt = now,
        };
        _dbContext.PhonePushRegistrations.Add(registration);
        await _registrations.MutateAsync(Request, registration, () => Task.CompletedTask,
            afterCommit: () => _audit.RecordAsync(AuditActions.PushDeviceRegistered, "PushRegistration", registration.Id, registration.DeviceLabel, registration.App, cancellationToken: cancellationToken),
            touchEntity: false, cancellationToken: cancellationToken);
        _registrations.EmitETag(Response, registration);
        return CreatedAtAction(nameof(Get), new { id = registration.Id }, Resource(registration));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var registration = await MineAsync(id, cancellationToken);
        _registrations.EmitETag(Response, registration);
        return Ok(Resource(registration));
    }

    [HttpHead("{id:guid}")]
    public async Task<IActionResult> GetHead(Guid id, CancellationToken cancellationToken)
    {
        _registrations.EmitETag(Response, await MineAsync(id, cancellationToken));
        return NoContent();
    }

    /// <summary>Refreshes the device's keys, address and label (a distributor may hand out new ones), with If-Match.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Refresh(Guid id, [FromBody] RegistrationRequest request, CancellationToken cancellationToken)
    {
        _registrations.RequireIfMatch(Request);
        var registration = await MineAsync(id, cancellationToken);
        Validate(request, requireApp: false, _development);
        if (request.Endpoint != registration.Endpoint
            && await _dbContext.PhonePushRegistrations.AnyAsync(r => r.Endpoint == request.Endpoint && r.Id != id, cancellationToken))
        {
            throw new PushEndpointTakenException();
        }

        await _registrations.MutateAsync(Request, registration, () =>
        {
            registration.DeviceLabel = request.DeviceLabel.Trim();
            registration.Endpoint = request.Endpoint;
            registration.P256dh = request.P256dh;
            registration.Auth = request.Auth;
            registration.LastRegisteredAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        },
        afterCommit: () => _audit.RecordAsync(AuditActions.PushDeviceUpdated, "PushRegistration", registration.Id, registration.DeviceLabel, registration.App, cancellationToken: cancellationToken),
        cancellationToken: cancellationToken);
        _registrations.EmitETag(Response, registration);
        return Ok(Resource(registration));
    }

    /// <summary>Removes the device's registration (sign-out, a deleted profile), with If-Match.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Unregister(Guid id, CancellationToken cancellationToken)
    {
        _registrations.RequireIfMatch(Request);
        var registration = await MineAsync(id, cancellationToken);
        await _registrations.MutateAsync(Request, registration, () =>
        {
            _dbContext.PhonePushRegistrations.Remove(registration);
            return Task.CompletedTask;
        },
        afterCommit: () => _audit.RecordAsync(AuditActions.PushDeviceRemoved, "PushRegistration", registration.Id, registration.DeviceLabel, registration.App, cancellationToken: cancellationToken),
        touchEntity: false, cancellationToken: cancellationToken);
        return NoContent();
    }

    // The caller's own row, or 404: another user's registration is not even acknowledged to exist.
    private async Task<PhonePushRegistration> MineAsync(Guid id, CancellationToken cancellationToken) =>
        _currentUser.UserId is { } userId
        && await _dbContext.PhonePushRegistrations.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken) is { } registration
            ? registration
            : throw new PushRegistrationNotFoundException();

    // https only: the server posts to whatever address a device names, so a plain-http or internal address would let
    // a user aim it at the installation's own services. Loopback http is accepted in Development alone, where a test
    // or a developer's local push server listens on it.
    private static void Validate(RegistrationRequest request, bool requireApp, bool development)
    {
        var valid = (!requireApp || request.App.Trim() is { Length: > 0 and <= LabelMax })
            && request.DeviceLabel.Trim() is { Length: > 0 and <= LabelMax }
            && request.Endpoint.Length <= EndpointMax
            && Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
            && (endpoint.Scheme == Uri.UriSchemeHttps || (development && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            && request.P256dh is { Length: > 0 and <= KeyMax } && DecodedLength(request.P256dh) is 65
            && request.Auth is { Length: > 0 and <= KeyMax } && DecodedLength(request.Auth) is 16;
        if (!valid)
        {
            throw new InvalidPushRegistrationException();
        }
    }

    // RFC 8291's keys, checked here rather than at the first push: an uncompressed P-256 point (65 bytes) and a 16-byte
    // auth secret, base64url. A registration whose keys cannot encrypt would otherwise fail silently on every push.
    private static int DecodedLength(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? written : -1;
    }

    private RegistrationResource Resource(PhonePushRegistration r) => new()
    {
        Id = r.Id,
        App = r.App,
        DeviceLabel = r.DeviceLabel,
        CreatedAt = r.CreatedAt,
        LastRegisteredAt = r.LastRegisteredAt,
        // One rel per resource, the method says which action (ADR 0719): GET reads it, PUT refreshes, DELETE removes.
        Links = [new Link("self", Url.Action(nameof(Get), new { id = r.Id })!, "GET")],
    };
}

/// <summary>The installation's push settings for phones (SimplArchiveMobile #20).</summary>
public sealed class PhonePushOptions
{
    public const string SectionName = "PhonePush";

    /// <summary>The address of the installation's UnifiedPush server, which the phone's distributor app connects to.</summary>
    public string? ServerUrl { get; set; }
}
