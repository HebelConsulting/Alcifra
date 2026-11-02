using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Users;

/// <summary>
/// The one-time, short-lived link to a newly enrolled device's configuration profile (#1970 slice 2, schema approved by
/// the owner 2026-10-29/31).
/// </summary>
/// <remarks>
/// <para>
/// The profile carries the device's password, which the server otherwise never keeps: so it is stored SEALED with the
/// installation's data-protection keys, for about ten minutes, and only the SHA-256 of the link's token is stored. The
/// token itself exists only in the link and its QR code.
/// </para>
/// <para>
/// <b>Not concurrency-tracked, by the owner's exemption (2026-10-29):</b> a machine-owned row. Opening the link claims it
/// atomically (a compare-and-swap on <see cref="TakenCount"/>, the last take deleting it; ADR 1028), and a sweep removes
/// the expired ones; no person ever edits one, so there is no lost update to prevent.
/// </para>
/// </remarks>
public class ProtocolDeviceHandOver : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>Lowercase hex SHA-256 of the link's token.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The profile, sealed with the shared data-protection keys.</summary>
    public required byte[] SealedProfile { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// How often the link has handed out the profile (ADR 1028, schema approved by the owner 2026-11-01): a browser
    /// that cannot install a profile hands it to one that can, which fetches the link again, so it works for a short
    /// grace after the first take, a few times at most.
    /// </summary>
    public int TakenCount { get; set; }
}
