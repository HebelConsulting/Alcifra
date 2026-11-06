using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// The two invariants the manuals feature rests on (ADRs 0891/0892), enforced at the one door every write path uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The standard repository cannot be deleted</b> — neither put in the recycle bin nor removed. The core files
/// what it brings there; a tenant without one files nothing. The remedy is to choose another one first.
/// </para>
/// <para>
/// <b>A core-owned mask</b> (the manuals folder's) is put on a document only by the core, never taken off or
/// changed, and worn by at most one live document per tenant. The core finds the folder by that mask, so each of the
/// three is the same fact seen from a different side: there is exactly one manuals folder, and the core made it.
/// </para>
/// <para>
/// There was no precedent for a mask only the system may ASSIGN — <c>Mask.UserCreatable</c> keeps a mask out of the
/// pickers and the create endpoints, and <c>ImmutableStructuralMasks</c> forbids changing AWAY from a mask, and both
/// are used here too — so the assignment half is an explicit grant: the filing path names the one document it is
/// creating (<see cref="AlcifraDbContext.PermitCoreOwnedMask"/>) and everything else is refused. A grant names a
/// DOCUMENT rather than switching the rule off for a save, so it cannot leak to an unrelated write in the same unit.
/// </para>
/// </remarks>
public static class CoreOwnedDocumentInvariants
{
    public static async Task EnforceStandardRepositoryAsync(AlcifraDbContext dbContext, CancellationToken cancellationToken)
    {
        var leaving = dbContext.ChangeTracker.Entries<Document>()
            .Where(e => e.Entity.ParentId == null
                && (e.State == EntityState.Deleted
                    || (e.State == EntityState.Modified
                        && e.Property(d => d.DeletedAt).IsModified
                        && e.Entity.DeletedAt is not null
                        && e.Property(d => d.DeletedAt).OriginalValue is null)))
            .Select(e => e.Entity)
            .ToList();

        foreach (var document in leaving)
        {
            if (await IsStandardRepositoryAsync(dbContext, document.Id, cancellationToken))
            {
                throw new StandardRepositoryProtectedException(document.Name);
            }
        }
    }

    /// <summary>Whether <paramref name="documentId"/> is its tenant's standard repository — asked by every path that
    /// deletes a root without SaveChanges (a bulk claim, a purge), so they refuse with the same answer.</summary>
    public static Task<bool> IsStandardRepositoryAsync(AlcifraDbContext dbContext, Guid documentId, CancellationToken cancellationToken) =>
        // Tenants carry no tenant filter; the id is unique across them, so no TenantId test is needed either.
        dbContext.Tenants.AnyAsync(t => t.StandardRepositoryId == documentId, cancellationToken);

    /// <summary>The core-owned-mask rules for one added or modified document; <paramref name="maskId"/> is its mask now.</summary>
    public static async Task EnforceCoreOwnedMaskAsync(
        AlcifraDbContext dbContext, Document document, Guid? maskId, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(document);
        Guid? originalMaskId = null;
        if (entry.State == EntityState.Modified && entry.Property(d => d.MaskVersionId).IsModified
            && entry.Property(d => d.MaskVersionId).OriginalValue is var originalVersion && originalVersion != Guid.Empty)
        {
            originalMaskId = await MaskIdOfAsync(dbContext, originalVersion, cancellationToken);
            if (originalMaskId is { } original && WellKnownMaskIds.CoreOwnedMasks.Contains(original) && maskId != original)
            {
                throw CoreOwnedMaskException.CannotChange(document.Name, await NameOfAsync(dbContext, document.TenantId, original, cancellationToken));
            }
        }

        if (maskId is not { } current || !WellKnownMaskIds.CoreOwnedMasks.Contains(current))
        {
            return;
        }

        var gaining = entry.State == EntityState.Added
            || (entry.Property(d => d.MaskVersionId).IsModified && originalMaskId != current);
        var restoring = entry.State == EntityState.Modified && entry.Property(d => d.DeletedAt).IsModified
            && document.DeletedAt is null;

        if (gaining && !dbContext.IsCoreOwnedMaskPermitted(document.Id))
        {
            throw CoreOwnedMaskException.NotAssignable(document.Name, await NameOfAsync(dbContext, document.TenantId, current, cancellationToken));
        }

        if ((gaining || restoring) && document.DeletedAt is null
            && await dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(d => d.TenantId == document.TenantId && d.Id != document.Id && d.DeletedAt == null
                    && dbContext.MaskVersions.IgnoreQueryFilters().Any(v => v.Id == d.MaskVersionId && v.MaskId == current),
                    cancellationToken))
        {
            throw CoreOwnedMaskException.OnlyOne(document.Name, await NameOfAsync(dbContext, document.TenantId, current, cancellationToken));
        }
    }

    /// <summary>
    /// The Administration root and its Users folder are the archive's own (ADR 1066): neither is deleted, renamed or moved,
    /// and a reference in Users is placed or removed only by the core, to a personal space. Children are the containment
    /// rules' business: the root admits only Users, Users admits nothing, and both masks need the core-owned grant.
    /// </summary>
    public static async Task EnforceProtectedFoldersAsync(AlcifraDbContext dbContext, CancellationToken cancellationToken)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<Document>()
                     .Where(e => e.State is EntityState.Deleted or EntityState.Modified).ToList())
        {
            var what = entry.State == EntityState.Deleted ? "deleted"
                : entry.Property(d => d.DeletedAt).IsModified && entry.Entity.DeletedAt is not null ? "deleted"
                : entry.Property(d => d.Name).IsModified ? "renamed"
                : entry.Property(d => d.ParentId).IsModified ? "moved"
                : null;
            if (what is null)
            {
                continue;
            }

            // The mask it WORE: a change of mask is refused above, so the original is the one that matters here.
            var worn = entry.Property(d => d.MaskVersionId).OriginalValue;
            if (worn != Guid.Empty
                && await MaskIdOfAsync(dbContext, worn, cancellationToken) is { } maskId
                && WellKnownMaskIds.ProtectedCoreFolders.Contains(maskId))
            {
                throw CoreOwnedFolderProtectedException.CannotChange(entry.Entity.Name, what);
            }
        }

        foreach (var entry in dbContext.ChangeTracker.Entries<DocumentReference>()
                     .Where(e => e.State is EntityState.Added or EntityState.Deleted).ToList())
        {
            var folder = await MaskOfDocumentAsync(dbContext, entry.Entity.ParentFolderId, cancellationToken);
            if (folder is not { } folderMaskId || !WellKnownMaskIds.ProtectedCoreFolders.Contains(folderMaskId))
            {
                continue;
            }

            var permitted = folderMaskId == WellKnownMaskIds.Users
                && dbContext.TakeCoreOwnedReferenceGrant(entry.Entity.Id)
                && (entry.State == EntityState.Deleted
                    || await MaskOfDocumentAsync(dbContext, entry.Entity.TargetDocumentId, cancellationToken) == WellKnownMaskIds.UserFolder);
            if (!permitted)
            {
                var name = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
                    .Where(d => d.Id == entry.Entity.ParentFolderId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
                throw CoreOwnedFolderProtectedException.ReferencesAreTheArchives(name ?? "Users");
            }
        }
    }

    private static Task<Guid?> MaskOfDocumentAsync(AlcifraDbContext dbContext, Guid documentId, CancellationToken cancellationToken) =>
        dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .Where(d => d.Id == documentId)
            .Join(dbContext.MaskVersions.IgnoreQueryFilters(), d => d.MaskVersionId, v => v.Id, (_, v) => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);

    private static Task<Guid?> MaskIdOfAsync(AlcifraDbContext dbContext, Guid maskVersionId, CancellationToken cancellationToken) =>
        dbContext.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<string> NameOfAsync(AlcifraDbContext dbContext, Guid tenantId, Guid maskId, CancellationToken cancellationToken) =>
        await dbContext.MaskVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId && v.MaskId == maskId && v.IsCurrent)
            .Select(v => v.Name)
            .FirstOrDefaultAsync(cancellationToken)
        ?? Alcifra.Localization.CoreOwnedNames.DocumentationMask;
}
