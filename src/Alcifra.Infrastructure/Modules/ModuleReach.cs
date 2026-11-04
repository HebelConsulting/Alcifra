using Alcifra.Application.Abstractions;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// What a module may read (ADR 0736, ADR 1045): what its principal's grants allow, and, inside a person's space, the folder
/// that person made with one of the module's masks and what that folder holds (#2132, owner 2026-11-03). Making the folder
/// is the invitation; nothing else of the space is reached, and the reach ends the moment the folder moves out, is
/// deleted or changes its mask, because it is computed, never stored.
/// Beyond a person's space too (ADR 1046): a document of its own masks referenced from a folder of its own that it can
/// see, and one such a document names in a reference field.
/// </summary>
/// <remarks>
/// One rule for the facade's read gate and for the populate step's question "may the module act on this subject at all",
/// so a hook is never run where the module would read nothing. Writes stay ungated and keep the caller's attribution.
/// </remarks>
public static class ModuleReach
{
    // What the folder's invitation allows a module to read: the documents and their content.
    private static readonly EffectiveRights Invited = new(
        CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false, CanDelete: false,
        CanCreateSubItems: false, CanManagePermissions: false, CanMove: false, CanAnnotate: false);

    // A personal space is never deeper than this; the walk stops rather than trust a cycle the invariants forbid.
    private const int MaxDepth = 64;

    /// <summary>The module principal's rights on each document, with the invitation of a folder of its own in a personal space added.</summary>
    public static async Task<IReadOnlyDictionary<Guid, EffectiveRights>> ForPrincipalAsync(
        AlcifraDbContext db, IEffectiveRightsCalculator rights, IReadOnlySet<Guid> moduleMasks, Guid principalId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var granted = await rights.GetEffectiveRightsForManyForServiceAccountAsync(principalId, documentIds, cancellationToken);
        var missing = documentIds.Where(id => !(granted.TryGetValue(id, out var r) && r.CanSee)).ToList();
        if (missing.Count == 0 || moduleMasks.Count == 0)
        {
            return granted;
        }

        var invited = await WithinOwnFoldersAsync(db, moduleMasks, missing, cancellationToken);
        var open = missing.Where(id => !invited.Contains(id)).ToList();
        if (open.Count > 0)
        {
            invited.UnionWith(await ReferencedFromOwnFoldersAsync(db, rights, moduleMasks, principalId, open, cancellationToken));
            open.RemoveAll(invited.Contains);
        }

        if (open.Count > 0)
        {
            invited.UnionWith(await NamedByOwnDocumentsAsync(db, rights, moduleMasks, principalId, open, cancellationToken));
        }

        if (invited.Count == 0)
        {
            return granted;
        }

        var merged = new Dictionary<Guid, EffectiveRights>(granted);
        foreach (var id in invited)
        {
            merged[id] = Invited;
        }

        return merged;
    }

    /// <summary>Whether <paramref name="moduleId"/> may see the document: false where it was never activated here.</summary>
    public static async Task<bool> MaySeeAsync(
        AlcifraDbContext db, IEffectiveRightsCalculator rights, IModuleMaskDeclarations masks, string moduleId, Guid documentId,
        CancellationToken cancellationToken)
    {
        if (await ModulePrincipal.FindAsync(db, moduleId, cancellationToken) is not { } principal)
        {
            return false;
        }

        var visible = await ForPrincipalAsync(db, rights, masks.MasksOf(moduleId), principal.Id, [documentId], cancellationToken);
        return visible.TryGetValue(documentId, out var r) && r.CanSee;
    }

    /// <summary>
    /// Documents of the module's own masks referenced from a folder of its own that it can see (ABI 1.29, ADR 1046): placing
    /// the reference is the invitation, so a video can be prepared anywhere and offered in a channel, and removing it ends
    /// the reach. A document of any other mask stays out of reach, wherever it is referenced.
    /// </summary>
    private static async Task<HashSet<Guid>> ReferencedFromOwnFoldersAsync(
        AlcifraDbContext db, IEffectiveRightsCalculator rights, IReadOnlySet<Guid> moduleMasks, Guid principalId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var masks = moduleMasks.ToList();
        var links = await (
                from r in db.DocumentReferences
                where documentIds.Contains(r.TargetDocumentId)
                join target in db.Documents on r.TargetDocumentId equals target.Id
                join targetMask in db.MaskVersions on target.MaskVersionId equals targetMask.Id
                where masks.Contains(targetMask.MaskId)
                join folder in db.Documents on r.ParentFolderId equals folder.Id
                join folderMask in db.MaskVersions on folder.MaskVersionId equals folderMask.Id
                where masks.Contains(folderMask.MaskId)
                select new { r.TargetDocumentId, r.ParentFolderId })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return [];
        }

        var folders = links.Select(l => l.ParentFolderId).Distinct().ToList();
        var seen = await rights.GetEffectiveRightsForManyForServiceAccountAsync(principalId, folders, cancellationToken);
        var own = await WithinOwnFoldersAsync(db, moduleMasks, folders, cancellationToken);
        return [.. links
            .Where(l => own.Contains(l.ParentFolderId) || (seen.TryGetValue(l.ParentFolderId, out var r) && r.CanSee))
            .Select(l => l.TargetDocumentId)];
    }

    /// <summary>
    /// Documents of the module's own masks that a document of its own, which it can see, names in a reference field (ABI
    /// 1.29, ADR 1046): a video's thumbnail, wherever the thumbnail lies. One step, never a chain.
    /// </summary>
    private static async Task<HashSet<Guid>> NamedByOwnDocumentsAsync(
        AlcifraDbContext db, IEffectiveRightsCalculator rights, IReadOnlySet<Guid> moduleMasks, Guid principalId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var masks = moduleMasks.ToList();
        var own = await (
                from d in db.Documents
                where documentIds.Contains(d.Id)
                join v in db.MaskVersions on d.MaskVersionId equals v.Id
                where masks.Contains(v.MaskId)
                select d.Id)
            .ToListAsync(cancellationToken);
        if (own.Count == 0)
        {
            return [];
        }

        var values = own.Select(id => id.ToString("D")).ToList();
        var namers = await (
                from value in db.FieldValues
                where values.Contains(value.Value)
                join field in db.FieldDefinitions on value.FieldDefinitionId equals field.Id
                where field.DataType == FieldDataType.DocumentReference
                join namer in db.Documents on value.DocumentId equals namer.Id
                join namerMask in db.MaskVersions on namer.MaskVersionId equals namerMask.Id
                where masks.Contains(namerMask.MaskId)
                select new { value.Value, value.DocumentId })
            .ToListAsync(cancellationToken);
        if (namers.Count == 0)
        {
            return [];
        }

        var owners = namers.Select(n => n.DocumentId).Distinct().ToList();
        var granted = await rights.GetEffectiveRightsForManyForServiceAccountAsync(principalId, owners, cancellationToken);
        var visible = owners.Where(id => granted.TryGetValue(id, out var r) && r.CanSee).ToHashSet();
        var rest = owners.Where(id => !visible.Contains(id)).ToList();
        if (rest.Count > 0)
        {
            visible.UnionWith(await WithinOwnFoldersAsync(db, moduleMasks, rest, cancellationToken));
            visible.UnionWith(await ReferencedFromOwnFoldersAsync(db, rights, moduleMasks, principalId, rest, cancellationToken));
        }

        return [.. namers.Where(n => visible.Contains(n.DocumentId)).Select(n => Guid.Parse(n.Value))];
    }

    /// <summary>
    /// The documents that lie in a personal space at or below a folder wearing one of <paramref name="moduleMasks"/>. The
    /// walk goes up from each document, one query per level for all of them, and stops at the space's root.
    /// </summary>
    private static async Task<HashSet<Guid>> WithinOwnFoldersAsync(
        AlcifraDbContext db, IReadOnlySet<Guid> moduleMasks, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<Guid, (Guid? ParentId, Guid? MaskId)>();
        var wanted = documentIds.ToList();
        for (var depth = 0; depth < MaxDepth && wanted.Count > 0; depth++)
        {
            var level = await (
                    from d in db.Documents
                    where wanted.Contains(d.Id) && d.PersonalRootOwnerId != null
                    join v in db.MaskVersions on d.MaskVersionId equals (Guid?)v.Id into versions
                    from v in versions.DefaultIfEmpty()
                    select new { d.Id, d.ParentId, MaskId = (Guid?)v.MaskId })
                .ToListAsync(cancellationToken);
            foreach (var node in level)
            {
                nodes[node.Id] = (node.ParentId, node.MaskId);
            }

            wanted = [.. level.Where(n => n.ParentId is { } p && !nodes.ContainsKey(p)).Select(n => n.ParentId!.Value).Distinct()];
        }

        var reached = new HashSet<Guid>();
        foreach (var start in documentIds)
        {
            Guid? at = start;
            for (var depth = 0; depth < MaxDepth && at is { } id && nodes.TryGetValue(id, out var node); depth++)
            {
                if (node.MaskId is { } mask && moduleMasks.Contains(mask))
                {
                    reached.Add(start);
                    break;
                }

                at = node.ParentId;
            }
        }

        return reached;
    }
}
