using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Masks;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// A document's mask may not change while something that chose it by its mask holds it (#2214, ADR 1074): a folder whose
/// admission binds its references (ABI 1.29, ADR 1046), or a reference field that names the masks it accepts (ABI 2.1,
/// ADR 1068). The other half of those two checks, which ask only when a reference or a value is written: a playlist's
/// video or a video's thumbnail would otherwise become something its holder never admits, behind its back.
/// </summary>
/// <remarks>
/// Bounded on purpose, the owner's decision on #2214: an ordinary reference or a field naming no masks holds nothing, so a
/// shortcut never keeps a document from being re-typed. A holder in the recycle bin still holds, since restoring it asks
/// nothing again. A change keeping the mask (a newer version of it) is no change here. Every other save costs one
/// ChangeTracker pass.
/// </remarks>
public static class MaskHeldByReferenceInvariants
{
    public static async Task EnforceAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations? masks, IMaskContainmentProvider containment, CancellationToken cancellationToken)
    {
        var remasked = dbContext.ChangeTracker.Entries<Document>()
            .Where(e => e.State == EntityState.Modified && e.Property(d => d.MaskVersionId).IsModified)
            .Select(e => (e.Entity, Original: e.Property(d => d.MaskVersionId).OriginalValue))
            .ToList();
        foreach (var (document, original) in remasked)
        {
            var mask = await MaskOfVersionAsync(dbContext, document.MaskVersionId, cancellationToken);
            if (mask == await MaskOfVersionAsync(dbContext, original, cancellationToken))
            {
                continue;
            }

            var rules = await containment.ForAsync(dbContext, document.TenantId, cancellationToken);
            var maskName = mask is { } m ? rules.NameOf(m) : string.Empty;
            await RefuseHoldingFolderAsync(dbContext, masks, rules, document, mask, maskName, cancellationToken);
            await RefuseHoldingFieldAsync(dbContext, document, mask, maskName, cancellationToken);
        }
    }

    private static async Task RefuseHoldingFolderAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations? masks, MaskContainmentRules rules, Document document, Guid? mask,
        string maskName, CancellationToken cancellationToken)
    {
        if (masks is not { ReferencesObeyAdmission.Count: > 0 })
        {
            return;
        }

        var removed = Removed<DocumentReference>(dbContext, r => r.Id);
        var documentId = document.Id;
        var folders = await (
                from reference in dbContext.DocumentReferences.IgnoreQueryFilters(["TenantFilter"])
                where reference.TargetDocumentId == documentId
                join folder in dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"]) on reference.ParentFolderId equals folder.Id
                join version in dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"]) on folder.MaskVersionId equals version.Id
                select new { reference.Id, folder.Name, version.MaskId })
            .ToListAsync(cancellationToken);
        if (folders.FirstOrDefault(f => !removed.Contains(f.Id) && masks.ReferencesObeyAdmission.Contains(f.MaskId)
                && (mask is not { } m || !rules.AdmittedBy(f.MaskId).Contains(m))) is { } holder)
        {
            throw MaskHeldByReferenceException.ByFolder(document.Name, maskName, holder.Name);
        }
    }

    private static async Task RefuseHoldingFieldAsync(
        AlcifraDbContext dbContext, Document document, Guid? mask, string maskName, CancellationToken cancellationToken)
    {
        var removed = Removed<FieldValue>(dbContext, v => v.Id);
        var value = document.Id.ToString("D");
        var holders = await (
                from fieldValue in dbContext.FieldValues
                where fieldValue.Value == value
                join field in dbContext.FieldDefinitions on fieldValue.FieldDefinitionId equals field.Id
                where field.DataType == FieldDataType.DocumentReference
                    && dbContext.FieldDefinitionAcceptedMasks.Any(a => a.FieldDefinitionId == field.Id)
                    && !dbContext.FieldDefinitionAcceptedMasks.Any(a => a.FieldDefinitionId == field.Id && a.MaskId == mask)
                join holder in dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]) on fieldValue.DocumentId equals holder.Id
                select new { fieldValue.Id, Field = field.Name, Holder = holder.Name })
            .ToListAsync(cancellationToken);
        if (holders.FirstOrDefault(h => !removed.Contains(h.Id)) is { } held)
        {
            throw MaskHeldByReferenceException.ByField(document.Name, maskName, held.Field, held.Holder);
        }
    }

    // What this very save removes no longer holds: taking a video out of its playlist and re-typing it is one act.
    private static HashSet<Guid> Removed<T>(AlcifraDbContext dbContext, Func<T, Guid> id)
        where T : class =>
        [.. dbContext.ChangeTracker.Entries<T>().Where(e => e.State == EntityState.Deleted).Select(e => id(e.Entity))];

    private static async Task<Guid?> MaskOfVersionAsync(AlcifraDbContext dbContext, Guid maskVersionId, CancellationToken cancellationToken) =>
        maskVersionId == Guid.Empty
            ? null
            : dbContext.ChangeTracker.Entries<MaskVersion>().FirstOrDefault(e => e.Entity.Id == maskVersionId)?.Entity.MaskId
                ?? await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
                    .Where(v => v.Id == maskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
}
