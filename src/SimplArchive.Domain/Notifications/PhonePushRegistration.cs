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

    /// <summary>The RFC 8030 push endpoint (the distributor's address). Opaque, and never logged: it identifies a device.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The device's P-256 public key (RFC 8291).</summary>
    public required string P256dh { get; set; }

    /// <summary>The device's auth secret (RFC 8291).</summary>
    public required string Auth { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the device last registered or refreshed; a registration not refreshed for long is pruned.</summary>
    public DateTimeOffset LastRegisteredAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
