using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Masks;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// A folder mask that says so admits a reference only to what it admits as a child (ABI 1.29, ADR 1046). Here because a
/// reference is placed by the workbench, the tool and the module facade alike. Opt-in: a folder may hold references to
/// what could never be its child, and modules outside this repository rely on that.
/// </summary>
public static class ReferenceAdmissionInvariants
{
    public static async Task EnforceAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations? masks, IMaskContainmentProvider containment, CancellationToken cancellationToken)
    {
        if (masks is not { ReferencesObeyAdmission.Count: > 0 })
        {
            return;
        }

        var placed = dbContext.ChangeTracker.Entries<DocumentReference>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        foreach (var reference in placed)
        {
            var folderMask = await MaskOfAsync(dbContext, reference.ParentFolderId, cancellationToken);
            if (folderMask is not { } folder || !masks.ReferencesObeyAdmission.Contains(folder))
            {
                continue;
            }

            var rules = await containment.ForAsync(dbContext, reference.TenantId, cancellationToken);
            var targetName = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
                .Where(d => d.Id == reference.TargetDocumentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
            rules.VerifyReference(targetName, await MaskOfAsync(dbContext, reference.TargetDocumentId, cancellationToken), folder);
        }
    }

    private static Task<Guid?> MaskOfAsync(AlcifraDbContext dbContext, Guid documentId, CancellationToken cancellationToken) =>
        dbContext.Documents.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.Id == documentId)
            .Join(dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"]), d => d.MaskVersionId, v => v.Id, (_, v) => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);
}
