using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// What a module may read (ADR 0736, ADR 1045): what its principal's grants allow, and, inside a person's space, the folder
/// that person made with one of the module's masks and what that folder holds (#2132, owner 2026-11-03). Making the folder
/// is the invitation; nothing else of the space is reached, and the reach ends the moment the folder moves out, is
/// deleted or changes its mask, because it is computed, never stored.
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
