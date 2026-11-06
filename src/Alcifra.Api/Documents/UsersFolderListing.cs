using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>
/// What the <c>Users</c> folder's listing says beyond any other folder's (ADR 1066). Its rows are the personal spaces of
/// everyone in the tenant, so a caller who may take one over is told so ON THE ROW, where every other row action is
/// found (ADR 0557: one read, many follows). The retired <c>GET /api/admin/personal-repositories</c> said the same thing
/// keyed by user id; the address stays the one <c>AdminController.TakeOverPersonalRepository</c> serves.
/// </summary>
internal static class UsersFolderListing
{
    /// <summary>Whether <paramref name="folderId"/> is the tenant's <c>Users</c> folder — by mask, never by name.</summary>
    public static Task<bool> IsUsersFolderAsync(AlcifraDbContext dbContext, Guid folderId, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => d.Id == folderId)
            .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => v.Id, (d, v) => v.MaskId)
            .AnyAsync(maskId => maskId == WellKnownMaskIds.Users, cancellationToken);

    /// <summary>
    /// Adds <c>take-over</c> to every row that is somebody else's personal root, when the caller holds <c>CanManageUsers</c>
    /// (ADR 0672: the lifecycle right, not the browsing one). The caller's own space never carries it — they already hold
    /// every right on it — and a caller without the right sees no rel rather than a button that answers 403 (ADR 0543).
    /// </summary>
    public static async Task AdvertiseTakeOverAsync(
        IReadOnlyList<Controllers.DocumentReferencesController.ReferenceResource> rows,
        AlcifraDbContext dbContext,
        ICurrentUserAccessor currentUser,
        IUserSystemRightsResolver systemRights,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || currentUser.UserId is not { } callerId
            || !(await systemRights.GetEffectiveSystemRightsAsync(callerId, cancellationToken)).CanManageUsers)
        {
            return;
        }

        var targetIds = rows.Select(r => r.Id).ToList();
        var owners = await dbContext.Documents
            .Where(d => targetIds.Contains(d.Id) && d.PersonalOfUserId != null && d.PersonalOfUserId != callerId)
            .Select(d => new { d.Id, OwnerId = d.PersonalOfUserId!.Value })
            .ToListAsync(cancellationToken);

        foreach (var owner in owners)
        {
            rows.First(r => r.Id == owner.Id).Links.Add(
                new Link("take-over", $"/api/admin/personal-repositories/{owner.OwnerId}/take-over", "POST"));
        }
    }
}
