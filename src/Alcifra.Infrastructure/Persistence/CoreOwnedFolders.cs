using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Domain.Documents;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// The folders the core creates for itself in every tenant (ADRs 0891/1066/1095): found by their core-owned masks,
/// never by name, created on the standard repository's behalf and named with the next free name when a person already
/// took the one the core would use. One implementation, because the five create the same way — the standard
/// repository's author, the core-owned grant, the name a sibling may already carry — and a copy is how one drifts.
/// </summary>
public static class CoreOwnedFolders
{
    /// <summary>
    /// The standard repository's author — the principal the core files on the tenant's behalf (a document needs exactly
    /// one creator). Null when the tenant has no standard repository yet: the folders need one, and the caller warns.
    /// </summary>
    public static async Task<(Guid? UserId, Guid? ServiceAccountId)?> AuthorAsync(
        AlcifraDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        var standardId = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.StandardRepositoryId)
            .SingleOrDefaultAsync(cancellationToken);
        var standard = standardId is { } id
            ? await dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
                .Where(d => d.Id == id && d.TenantId == tenantId)
                .Select(d => new { d.CreatedByUserId, d.CreatedByServiceAccountId })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        return standard is null
            ? null
            : (standard.CreatedByUserId, standard.CreatedByUserId is null ? standard.CreatedByServiceAccountId : null);
    }

    /// <summary>
    /// The next free name among <paramref name="parentId"/>'s children: the wanted name, or "(2)", "(3)", … when a
    /// sibling already carries it (a folder somebody called "Alcifra" is theirs, not the core's). A soft-deleted sibling
    /// still counts as having taken the name — the core's folders are recognised by their masks, so a fresh name is
    /// cheaper than a name that reappears over content the recycle bin still holds.
    /// </summary>
    public static async Task<string> FreeNameAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid? parentId, string wantedName, CancellationToken cancellationToken)
    {
        var taken = await dbContext.Documents
            .IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .Where(d => d.TenantId == tenantId && d.ParentId == parentId && (parentId != null || d.PersonalOfUserId == null))
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        if (!taken.Contains(wantedName, StringComparer.OrdinalIgnoreCase))
        {
            return wantedName;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{wantedName} ({n})";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Creates a core-owned folder beneath <paramref name="parentId"/> (null: a root), taking the next free name and
    /// saying so when the wanted one is taken. Null (and a Warning) when the tenant's mask catalogue does not have
    /// <paramref name="maskId"/> yet — the next start heals it.
    /// </summary>
    public static async Task<Document?> CreateAsync(
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

        var name = await FreeNameAsync(dbContext, tenantId, parentId, wantedName, cancellationToken);
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
}
