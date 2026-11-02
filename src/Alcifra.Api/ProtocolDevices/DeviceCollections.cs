using Microsoft.EntityFrameworkCore;
using Alcifra.Api.CalDav;
using Alcifra.Api.Errors.Exceptions.ProtocolDevices;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>A shared calendar or address book a device set to chosen collections can tick (#2090).</summary>
/// <param name="Kind"><c>calendar</c> or <c>contacts</c>.</param>
public sealed record DeviceCollectionChoice(Guid Id, string Name, string Kind, bool Ticked);

/// <summary>
/// The calendars and address books a device shows (#2090, owner decisions 2026-11-02): which SHARED collections the user
/// can tick — their own always show and are not listed — and the ticks a device's PUT stores.
/// </summary>
/// <remarks>
/// The choices are the collections the DAV server would list for the user on a device that shows all of them, less the
/// user's own (in their personal space, the task feeds, their own schedules): one rule with what a client then sees.
/// </remarks>
public sealed class DeviceCollections(AlcifraDbContext dbContext, IEffectiveRightsCalculator rights, IDavCollectionKindRegistry kinds)
{
    /// <summary>The shared collections the user can tick, each marked as the device has it; calendars first, then by name.</summary>
    public async Task<IReadOnlyList<DeviceCollectionChoice>> ChoicesAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var ticked = (await TickedAsync([deviceId], cancellationToken)).GetValueOrDefault(deviceId) ?? [];
        return [.. (await SharedAsync(userId, cancellationToken)).Select(c => c with { Ticked = ticked.Contains(c.Id) })];
    }

    /// <summary>The ticks of each device named, in one query.</summary>
    public async Task<Dictionary<Guid, List<Guid>>> TickedAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken) =>
        (await dbContext.ProtocolDeviceCollections.AsNoTracking()
            .Where(c => deviceIds.Contains(c.ProtocolDeviceId))
            .Select(c => new { c.ProtocolDeviceId, c.FolderId })
            .ToListAsync(cancellationToken))
        .GroupBy(c => c.ProtocolDeviceId)
        .ToDictionary(g => g.Key, g => g.Select(c => c.FolderId).ToList());

    /// <summary>
    /// Replaces the device's ticks with <paramref name="folderIds"/>, inside the device's own write (its <c>If-Match</c>
    /// guards the ticks, ADR 0878's tracked-parent exemption). An id that is not a shared collection the user can tick is
    /// refused, never stored silently.
    /// </summary>
    public async Task ReplaceAsync(ProtocolDevice device, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken)
    {
        var choosable = (await SharedAsync(device.UserId, cancellationToken)).Select(c => c.Id).ToHashSet();
        if (folderIds.FirstOrDefault(id => !choosable.Contains(id)) is var unknown && unknown != Guid.Empty)
        {
            throw new ProtocolDeviceCollectionUnknownException();
        }

        var stored = await dbContext.ProtocolDeviceCollections.Where(c => c.ProtocolDeviceId == device.Id).ToListAsync(cancellationToken);
        dbContext.ProtocolDeviceCollections.RemoveRange(stored.Where(c => !folderIds.Contains(c.FolderId)));
        dbContext.ProtocolDeviceCollections.AddRange(folderIds.Distinct()
            .Where(id => stored.All(c => c.FolderId != id))
            .Select(id => new ProtocolDeviceCollection { TenantId = device.TenantId, ProtocolDeviceId = device.Id, FolderId = id }));
    }

    // Every collection the user sees on a device showing all, less their own: only folders count, and only those outside
    // the personal space (the task feeds and schedules have no folder and are always shown).
    private async Task<List<DeviceCollectionChoice>> SharedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var shared = new List<DeviceCollectionChoice>();
        foreach (var (protocol, kind) in new[] { (DavProtocol.CalDav, "calendar"), (DavProtocol.CardDav, "contacts") })
        {
            var collections = await DavTree.CollectionsAsync(dbContext, rights, userId, protocol, kinds, cancellationToken);
            var ids = collections.Select(c => c.FolderId).ToList();
            var own = await dbContext.Documents.AsNoTracking()
                .Where(d => ids.Contains(d.Id))
                .Join(dbContext.Documents.AsNoTracking(), d => d.ParentId, p => (Guid?)p.Id, (d, p) => new { d.Id, p.PersonalOfUserId })
                .Where(r => r.PersonalOfUserId == userId)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            var folders = await dbContext.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
            shared.AddRange(collections
                .Where(c => folders.Contains(c.FolderId) && !own.Contains(c.FolderId))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new DeviceCollectionChoice(c.FolderId, c.DisplayName, kind, false)));
        }

        return shared;
    }
}
