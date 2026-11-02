using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Users;

/// <summary>
/// A shared calendar or address book ticked on a device that shows only CHOSEN collections (#2090, schema approved by the
/// owner 2026-11-02): such a device shows the user's own collections always, and a shared one only while a row names it.
/// </summary>
/// <remarks>
/// <b>Not concurrency-tracked, by the owner's exemption (2026-11-02): a child guarded by a tracked parent.</b> A row is
/// only ever created or deleted (a tick on, a tick off), always by the device's own PUT, which carries the device's
/// <c>If-Match</c>; the device's token covers every change to its ticks, and no client addresses a single tick.
/// </remarks>
public class ProtocolDeviceCollection : ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid ProtocolDeviceId { get; set; }

    /// <summary>The calendar or address-book folder (a document) the tick names.</summary>
    public Guid FolderId { get; set; }
}
