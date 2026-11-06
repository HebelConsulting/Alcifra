using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// The tenant's <c>Administration</c> root and its <c>Users</c> folder (ADR 1066, #2180): real folders, found by their
/// core-owned masks, never by name. <c>Users</c> holds one reference per user's folder, which the core places when a
/// personal space is provisioned and reconciles at every start. Nothing here is a person's to change
/// (<see cref="CoreOwnedDocumentInvariants.EnforceProtectedFoldersAsync"/>), and only tenant administrators see it: the
/// root carries no grant, so the admin bypass is the only way in, as it was for the synthetic branch the clients drew.
/// </summary>
public static class AdministrationFolders
{
    public const string RootName = "Administration";
    public const string UsersName = "Users";

    /// <summary>
    /// <paramref name="documents"/> without the folders ADR 1066 keeps out of the protocol mounts: an administrator's WebDAV
    /// volume and mail account mirror the Repositories tree (ADRs 0509, 0809) with this one named exception, because a
    /// folder of references to other people's spaces is nothing a file browser or a mail client can do anything useful
    /// with. Asked by mask, like everything else about these folders; guarded beside the parity it is an exception to.
    /// </summary>
    public static IQueryable<Document> ExceptProtectedCoreFolders(this IQueryable<Document> documents, AlcifraDbContext dbContext)
    {
        var protectedMasks = WellKnownMaskIds.ProtectedCoreFolders.ToList();
        var versions = dbContext.MaskVersions;
        return documents.Where(d => !versions.Any(v => v.Id == d.MaskVersionId && protectedMasks.Contains(v.MaskId)));
    }

    /// <summary>The live folder wearing <paramref name="maskId"/> in <paramref name="tenantId"/>, or null.</summary>
    public static Task<Document?> FindAsync(AlcifraDbContext dbContext, Guid tenantId, Guid maskId, CancellationToken cancellationToken)
    {
        // Built outside the expression: a collection expression cannot sit inside one.
        var versions = dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"]);
        return dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.TenantId == tenantId && versions.Any(v => v.Id == d.MaskVersionId && v.MaskId == maskId))
            // By id, not by date: there is one live folder per mask and tenant (the core-owned rule), and the test
            // provider cannot order by a DateTimeOffset.
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Makes sure the tenant has its <c>Administration</c> root with <c>Users</c> beneath, and that <c>Users</c> references
    /// exactly the live personal spaces. Idempotent; a tenant without a standard repository gets nothing (and a Warning):
    /// the folders need a creator, and the standard repository's is the one the core already files on behalf of (ADR 0891).
    /// </summary>
    public static async Task EnsureAsync(AlcifraDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        var root = await FindAsync(dbContext, tenantId, WellKnownMaskIds.Administration, cancellationToken);
        var users = await FindAsync(dbContext, tenantId, WellKnownMaskIds.Users, cancellationToken);
        if (root is null || users is null)
        {
            var author = await AuthorAsync(dbContext, tenantId, cancellationToken);
            if (author is null)
            {
                logger.LogWarning(
                    "Tenant {TenantId} has no standard repository, so its Administration folder was not created. A tenant "
                    + "administrator can choose one in the tenant settings; the folder is created at the next start.", tenantId);
                return;
            }

            root ??= await CreateAsync(dbContext, tenantId, null, RootName, WellKnownMaskIds.Administration, author.Value, logger, cancellationToken);
            if (root is null)
            {
                return;
            }

            users ??= await CreateAsync(dbContext, tenantId, root.Id, UsersName, WellKnownMaskIds.Users, author.Value, logger, cancellationToken);
            if (users is null)
            {
                return;
            }
        }

        await ReconcileAsync(dbContext, tenantId, users, cancellationToken);
    }

    /// <summary>
    /// Places the reference for one just-provisioned personal space, when the tenant already has its <c>Users</c> folder;
    /// a tenant that has none yet gets the reference at the next start's reconciliation.
    /// </summary>
    public static async Task AddUserReferenceAsync(AlcifraDbContext dbContext, Guid tenantId, Guid personalRootId, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, tenantId, WellKnownMaskIds.Users, cancellationToken) is not { } users)
        {
            return;
        }

        var exists = await dbContext.DocumentReferences.IgnoreQueryFilters(["TenantFilter"])
            .AnyAsync(r => r.ParentFolderId == users.Id && r.TargetDocumentId == personalRootId, cancellationToken);
        if (!exists)
        {
            Place(dbContext, users, personalRootId);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ReconcileAsync(AlcifraDbContext dbContext, Guid tenantId, Document users, CancellationToken cancellationToken)
    {
        var spaces = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.TenantId == tenantId && d.ParentId == null && d.PersonalOfUserId != null)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
        var placed = await dbContext.DocumentReferences.IgnoreQueryFilters(["TenantFilter"])
            .Where(r => r.ParentFolderId == users.Id)
            .ToListAsync(cancellationToken);

        var wanted = spaces.ToHashSet();
        var have = placed.Select(r => r.TargetDocumentId).ToHashSet();
        foreach (var spaceId in spaces.Where(id => !have.Contains(id)))
        {
            Place(dbContext, users, spaceId);
        }

        foreach (var stale in placed.Where(r => !wanted.Contains(r.TargetDocumentId)))
        {
            dbContext.PermitCoreOwnedReference(stale.Id);
            dbContext.DocumentReferences.Remove(stale);
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // The reference's author is the folder's: the core acts on the tenant's behalf, and a row needs exactly one principal.
    private static void Place(AlcifraDbContext dbContext, Document users, Guid personalRootId)
    {
        var reference = new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = users.TenantId,
            ParentFolderId = users.Id,
            TargetDocumentId = personalRootId,
            CreatedByUserId = users.CreatedByUserId,
            CreatedByServiceAccountId = users.CreatedByUserId is null ? users.CreatedByServiceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PermitCoreOwnedReference(reference.Id);
        dbContext.DocumentReferences.Add(reference);
    }

    private static async Task<Document?> CreateAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid? parentId, string wantedName, Guid maskId,
        (Guid? UserId, Guid? ServiceAccountId) author, ILogger logger, CancellationToken cancellationToken)
    {
        var maskVersionId = await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == maskId && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (maskVersionId is null)
        {
            logger.LogWarning("Tenant {TenantId} has no '{Mask}' mask yet, so the folder was not created; the next start heals it.", tenantId, wantedName);
            return null;
        }

        // A sibling may already carry the name (a repository somebody called Administration): the folder is recognised by
        // its mask, so it takes the next free name and says so, as the manuals folder does.
        var taken = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .Where(d => d.TenantId == tenantId && d.ParentId == parentId && (parentId != null || d.PersonalOfUserId == null))
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        var name = wantedName;
        for (var n = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
        {
            name = $"{wantedName} ({n})";
        }

        if (!string.Equals(name, wantedName, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Tenant {TenantId}: a document named '{Taken}' already exists beside the archive's own folder, which was "
                + "created as '{Name}'. The folder is recognised by its type, not its name.", tenantId, wantedName, name);
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = maskVersionId.Value,
            CreatedByUserId = author.UserId,
            CreatedByServiceAccountId = author.ServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PermitCoreOwnedMask(folder.Id);
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);
        return folder;
    }

    private static async Task<(Guid? UserId, Guid? ServiceAccountId)?> AuthorAsync(AlcifraDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        var standardId = await dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.StandardRepositoryId).SingleOrDefaultAsync(cancellationToken);
        var standard = standardId is { } id
            ? await dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
                .Where(d => d.Id == id)
                .Select(d => new { d.CreatedByUserId, d.CreatedByServiceAccountId })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        return standard is null
            ? null
            : (standard.CreatedByUserId, standard.CreatedByUserId is null ? standard.CreatedByServiceAccountId : null);
    }
}
