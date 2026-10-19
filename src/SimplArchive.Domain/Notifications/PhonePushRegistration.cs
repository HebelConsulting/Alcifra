using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Notifications;

/// <summary>
/// One phone's push registration for one user (SimplArchiveMobile #20, slice B): the UnifiedPush endpoint a
/// notification is posted to, encrypted to the device (RFC 8291), when the user's notification preferences send that
/// type to the phone. Written by the device that owns it: registered on sign-in, refreshed, removed on sign-out.
/// </summary>
/// <remarks>
/// Concurrency-tracked by the owner's decision (2026-10-12), because one user may have several clients: a refresh
/// or a removal states the tag of the registration it read (ADR 0878).
/// </remarks>
public class PhonePushRegistration : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Whose phone it is; a registration is personal and dies with the account.</summary>
    public Guid UserId { get; set; }

    /// <summary>The registering app's package id, so a second app (a messenger, #1725) registers separately.</summary>
    public required string App { get; set; }

    /// <summary>What the user sees in their list of devices, such as "Pixel 10 Pro · Anna on demo".</summary>
    public required string DeviceLabel { get; set; }

    /// <summary>How pushes reach the device (owner-approved schema, 2026-10-19): <see cref="PushTransports.WebPush"/> through
    /// the device's distributor, or <see cref="PushTransports.Apns"/> through Apple's push service (iOS, core #1805).</summary>
    public string Transport { get; set; } = PushTransports.WebPush;

    /// <summary>For Web Push the RFC 8030 endpoint (the distributor's address); for APNs the device token, in hex. Opaque,
    /// and never logged: it identifies a device.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The device's P-256 public key (RFC 8291); Web Push only.</summary>
    public string? P256dh { get; set; }

    /// <summary>The device's auth secret (RFC 8291); Web Push only.</summary>
    public string? Auth { get; set; }

    /// <summary>Which APNs the token belongs to: <c>sandbox</c> (a development build) or <c>production</c> (TestFlight, the
    /// App Store); APNs only.</summary>
    public string? ApnsEnvironment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the device last registered or refreshed; a registration not refreshed for long is pruned.</summary>
    public DateTimeOffset LastRegisteredAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}

/// <summary>The ways a push reaches a phone (<see cref="PhonePushRegistration.Transport"/>).</summary>
public static class PushTransports
{
    public const string WebPush = "webpush";
    public const string Apns = "apns";

    /// <summary>The APNs environment of a development build's token (<see cref="PhonePushRegistration.ApnsEnvironment"/>).</summary>
    public const string Sandbox = "sandbox";

    /// <summary>The APNs environment of a TestFlight or App Store build's token.</summary>
    public const string Production = "production";
}
