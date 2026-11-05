namespace Alcifra.ModuleAbi;

/// <summary>
/// A module's part in a device's setup profile (ABI 1.27, core ADR 1036): the payloads it adds when a person takes the
/// hand-over profile of an enrolled device (the provisioning module's VPN and Wi-Fi, core #2101), and the word that a
/// device was revoked, so what the module issued for it can be revoked too.
/// </summary>
/// <remarks>
/// <para>
/// Declared through <see cref="IIndustryModule.DeviceProfiles"/>, null by default. The core asks every module that is
/// ACTIVE for the tenant, in load order, while it builds the profile, running the module as its own principal. The
/// profile is handed over once (core ADR 1031), which is what makes a one-time secret inside a payload acceptable.
/// </para>
/// <para>
/// <b>A module that throws fails the hand-over</b>: a profile missing a payload the person was promised is worse than
/// none, and the core says which module failed. A revocation that the module cannot act on at once is the module's to
/// retry; the core's revocation has already happened.
/// </para>
/// </remarks>
public interface IModuleDeviceProfiles
{
    /// <summary>The payloads to add to this device's profile; empty when the module has nothing for this person.</summary>
    Task<IReadOnlyList<ProfilePayload>> PayloadsAsync(DeviceProfileContext context, CancellationToken cancellationToken);

    /// <summary>The device was revoked in the core (after the commit); the module revokes what it issued for it.</summary>
    Task DeviceRevokedAsync(DeviceRevokedContext context, CancellationToken cancellationToken);
}

/// <summary>Who takes the profile, and for which device. No secret of the core travels here.</summary>
/// <param name="TenantId">The tenant the module acts in.</param>
/// <param name="UserId">The person whose device it is.</param>
/// <param name="UserEmail">Their address, the natural subject alternative name of a device certificate.</param>
/// <param name="DeviceId">The enrolled device.</param>
/// <param name="DeviceName">Its name, as the person reads it.</param>
/// <param name="WebHost">The installation's web host, for a payload that names it.</param>
/// <param name="PayloadUuid">The UUID the core gives a payload part of this device: stable per (device, part), so a
/// payload may reference another (a VPN payload naming its certificate payload) and a re-installed profile replaces
/// rather than duplicates.</param>
/// <param name="Services">The host's services, from which the module resolves its collaborators (the archive facade,
/// what it registered in <see cref="IIndustryModule.ConfigureServices"/>) — as a booking review does.</param>
public sealed record DeviceProfileContext(
    Guid TenantId, Guid UserId, string UserEmail, Guid DeviceId, string DeviceName, string WebHost, Func<string, string> PayloadUuid, IServiceProvider Services);

/// <summary>A device that was revoked: its ids, nothing else, since the row is gone; and the host's services.</summary>
public sealed record DeviceRevokedContext(Guid TenantId, Guid UserId, Guid DeviceId, IServiceProvider Services);

/// <summary>
/// One payload of a configuration profile: its <c>PayloadType</c>, a <paramref name="Part"/> name unique within the
/// module (the core derives the identifier and the UUID from it), the display name the person reads, and the payload's
/// own keys as plist XML elements (<c>&lt;key&gt;…&lt;/key&gt;&lt;string&gt;…&lt;/string&gt;</c>, nested dicts and arrays as
/// needed). The core adds <c>PayloadDisplayName</c>, <c>PayloadIdentifier</c>, <c>PayloadType</c>, <c>PayloadUUID</c> and
/// <c>PayloadVersion</c>; a module writes none of them.
/// </summary>
public sealed record ProfilePayload(string Type, string Part, string DisplayName, string BodyXml);
