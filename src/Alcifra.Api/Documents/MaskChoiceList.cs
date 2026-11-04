using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>One mask a document's picker offers; its <c>self</c> is where the mask's field definitions live.</summary>
public class MaskChoiceResource : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// What a document's mask picker offers (#2169, ADR 1049), decided here so no client derives it. A folder is offered what its
/// parent may create, the list New folder uses. A document is offered the freely assignable masks. Its own mask is always
/// among them, so a picker can show what it wears even where it could not choose it.
/// </summary>
public static class MaskChoiceList
{
    /// <summary>Whether a user may choose a mask for a document, as opposed to the mask being given by where it is or what it holds.</summary>
    public static bool IsFreelyAssignable(Guid maskId, bool isFolderMask, bool claimedByExtension) =>
        !isFolderMask
        && !claimedByExtension
        && !WellKnownMaskIds.AdmittingFolders.ContainsKey(maskId)
        && !WellKnownMaskIds.NotUserCreatable.Contains(maskId)
        && !WellKnownMaskIds.SealedContentMasks.ContainsKey(maskId);

    public static async Task<List<MaskChoiceResource>> ForAsync(
        AlcifraDbContext db, Guid documentId, Guid currentMaskId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.ParentId })
            .SingleAsync(cancellationToken);
        var rules = await MaskContainmentRules.LoadAsync(db, document.TenantId, cancellationToken);

        var ids = rules.IsFolderMask(currentMaskId)
            ? await FolderChoicesAsync(db, rules, document.ParentId, cancellationToken)
            : await DocumentChoicesAsync(db, cancellationToken);

        return [.. ids.Append(currentMaskId).Distinct()
            .Select(id => new MaskChoiceResource
            {
                Id = id,
                Name = rules.NameOf(id),
                Links = [new Link("self", $"/api/masks/{id}", "GET")],
            })
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static async Task<IEnumerable<Guid>> FolderChoicesAsync(
        AlcifraDbContext db, MaskContainmentRules rules, Guid? parentId, CancellationToken cancellationToken)
    {
        Guid? parentMask = parentId is { } id
            ? await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => d.Id == id)
                .Join(db.MaskVersions, d => d.MaskVersionId, v => v.Id, (d, v) => (Guid?)v.MaskId)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return rules.UserCreatableMasks.Where(mask => rules.IsFolderMask(mask) && rules.Allows(mask, parentMask));
    }

    private static async Task<IEnumerable<Guid>> DocumentChoicesAsync(AlcifraDbContext db, CancellationToken cancellationToken)
    {
        var masks = await db.Masks.Select(m => new { m.Id, m.IsFolderMask }).ToListAsync(cancellationToken);
        var claimed = (await db.MaskFileExtensions.Select(e => e.MaskId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        return masks.Where(m => IsFreelyAssignable(m.Id, m.IsFolderMask, claimed.Contains(m.Id))).Select(m => m.Id);
    }
}
