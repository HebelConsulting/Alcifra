using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.CalDav;

/// <summary>
/// Which collections the signed-in DEVICE shows (#2090): all of them, or — on a device set to chosen collections, such as
/// an iPhone or iPad set up through its profile — the user's own always and a shared one only while it is ticked.
/// </summary>
/// <remarks>
/// A collection is the user's OWN when it sits in their personal space (My Calendar, My Addressbook and any other they
/// made there), and so are the task feeds and their own schedules; those cannot be unticked (owner, 2026-11-02). The scope
/// narrows what the rights allow, never widens it: a ticked folder the user can no longer see is still not shown.
/// </remarks>
internal sealed record DavScope(bool Chosen, IReadOnlySet<Guid> Ticked)
{
    internal static readonly DavScope All = new(false, new HashSet<Guid>());

    /// <summary>Whether a collection is shown: every one on a device that shows all; else an own one, or a ticked one.</summary>
    internal bool Shows(Guid folderId, bool isOwn) => !Chosen || isOwn || Ticked.Contains(folderId);

    /// <summary>The scope of the device a DAV request signed in with; <see cref="All"/> where none is named.</summary>
    internal static async Task<DavScope> ForAsync(AlcifraDbContext db, Guid? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is not { } id
            || !await db.ProtocolDevices.AsNoTracking().AnyAsync(d => d.Id == id && d.CollectionsChosen, cancellationToken))
        {
            return All;
        }

        var ticked = await db.ProtocolDeviceCollections.AsNoTracking()
            .Where(c => c.ProtocolDeviceId == id)
            .Select(c => c.FolderId)
            .ToListAsync(cancellationToken);
        return new DavScope(true, ticked.ToHashSet());
    }
}
