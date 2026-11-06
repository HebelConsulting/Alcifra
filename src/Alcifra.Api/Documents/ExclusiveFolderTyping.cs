using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>
/// The item mask a module's EXCLUSIVE folder gives a file filed into it (ADR 1065). A Tube Channel admits only a Video and
/// a Video Thumbnail; an upload into it arrives maskless and was given Basic Entry at finalize, which the channel's
/// admission then refused — so no client could file a clip there without assigning the mask itself first. The rule is the
/// addressbook's and the calendar's (#665, ADR 0744): the destination says what the file is, not its bytes. Where the folder
/// admits several kinds, the masks' own accepted extensions (ADR 1046) tell them apart, and only an unambiguous answer counts.
/// </summary>
public static class ExclusiveFolderTyping
{
    /// <summary>The mask id of a document's parent, or null for a root or a maskless parent.</summary>
    public static Task<Guid?> ParentMaskIdAsync(AlcifraDbContext dbContext, Guid parentId, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => d.Id == parentId)
            .Select(d => dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault())
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The one admitted item mask of an exclusive folder that holds files like <paramref name="objectKey"/>, or null: an
    /// ordinary folder, a core typed folder (its items name no extensions), several candidates, or none.
    /// </summary>
    /// <remarks>
    /// Asked of the same containment rules the invariant enforces, so what is stamped here is what SaveChanges admits. A
    /// module mask without accepted extensions is never chosen: nothing says the file is one, and a wrong guess would be
    /// refused by the mask's own content rule (ADR 1046) or sit mistyped where the module reads it.
    /// </remarks>
    public static Guid? AdmittedItemMask(MaskContainmentRules rules, IModuleMaskDeclarations? modules, Guid? parentMaskId, string objectKey)
    {
        if (parentMaskId is not { } folderMaskId || modules is null || !rules.IsExclusiveFolder(folderMaskId))
        {
            return null;
        }

        var holders = rules.AdmittedBy(folderMaskId)
            .Where(m => !rules.IsFolderMask(m)
                && modules.AcceptedContentOf.TryGetValue(m, out var accepted)
                && accepted.Accepts(objectKey)
                && rules.Allows(m, folderMaskId))
            .ToList();
        return holders.Count == 1 ? holders[0] : null;
    }
}
