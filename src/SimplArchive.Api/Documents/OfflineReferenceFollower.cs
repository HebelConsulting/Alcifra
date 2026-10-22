using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// What an offline export brings in through references (ADR 0959, #1881): the phone's copy follows a reference
/// filed in its scope to the target, so a document placed into "My documents" from another repository is in the
/// copy. The repository export does not ask for this; it stays a restorable snapshot of its subtree.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reference to a document brings that document; a reference to a folder brings its whole subtree</b>, as if
/// chosen, with the visible folder chain above each target as metadata (as for an opened document), so the copy
/// places it where the tree has it. References inside what was brought in are followed the same way.
/// </para>
/// <para>
/// <b>Each target once, cycle-safe, and bounded:</b> a target already in the scope, or brought in earlier, is not
/// followed again; at most <see cref="MaxFollowedTargets"/> targets are followed per export, and past that the
/// references are still listed but their targets are not brought in (a Warning says so).
/// </para>
/// <para>
/// <b>The caller's rights decide</b>, as everywhere in the offline export: a target the caller may not see is not
/// brought in and its reference is not listed, as online (ADR 0765: what you cannot see is not named to you; owner
/// decision 2026-10-22); beneath a brought-in folder, what the caller cannot see is left out with its subtree.
/// </para>
/// </remarks>
public sealed class OfflineReferenceFollower(SimplArchiveDbContext dbContext, ILogger logger)
{
    /// <summary>How many reference targets one offline export follows at most.</summary>
    public const int MaxFollowedTargets = 500;

    /// <summary>A reference filed in the export's scope whose target the caller may see, with what the copy needs to list
    /// it when its target is absent.</summary>
    /// <param name="TargetName">The target's name.</param>
    /// <param name="TargetIsFolder">Whether the target is a folder (holds no confirmed version).</param>
    public sealed record FollowedReference(DocumentReference Reference, string TargetName, bool TargetIsFolder);

    /// <param name="BroughtIn">The targets and what the caller may see beneath them, parents before children.</param>
    /// <param name="Chains">The visible folders above the targets that are not otherwise in the export, metadata only.</param>
    /// <param name="Targets">The targets brought in (each is included even when it carries nothing readable).</param>
    /// <param name="Rights">The caller's rights on every document brought in.</param>
    /// <param name="References">Every reference filed in the scope or in what was brought in.</param>
    public sealed record Result(
        List<Document> BroughtIn,
        List<Document> Chains,
        IReadOnlySet<Guid> Targets,
        IReadOnlyDictionary<Guid, EffectiveRights> Rights,
        List<FollowedReference> References);

    /// <param name="scope">The anchor's visible subtree, as the export already holds it.</param>
    /// <param name="chain">The anchor's visible folder chain (already in the export, metadata only).</param>
    public async Task<Result> FollowAsync(Guid anchorId, IReadOnlyList<Document> scope, IReadOnlyCollection<Document> chain,
        OfflineExportAudience audience, CancellationToken cancellationToken)
    {
        var held = scope.Select(d => d.Id).ToHashSet();
        var broughtIn = new List<Document>();
        var targets = new HashSet<Guid>();
        var rights = new Dictionary<Guid, EffectiveRights>();
        var references = new List<FollowedReference>();
        var frontier = held.ToList();
        var boundReached = false;

        while (frontier.Count > 0)
        {
            // The references filed in the frontier whose target exists (a soft-deleted target is filtered out by the
            // query filter, as the online listing leaves it out).
            var filed = await dbContext.DocumentReferences
                .Where(r => frontier.Contains(r.ParentFolderId) && dbContext.Documents.Any(d => d.Id == r.TargetDocumentId))
                .Select(r => new
                {
                    Reference = r,
                    TargetName = dbContext.Documents.Where(d => d.Id == r.TargetDocumentId).Select(d => d.Name).FirstOrDefault(),
                    TargetHasVersions = dbContext.DocumentVersions.Any(v => v.DocumentId == r.TargetDocumentId && v.Status == DocumentVersionStatus.Confirmed),
                })
                .ToListAsync(cancellationToken);
            if (filed.Count == 0)
            {
                break;
            }

            var targetRights = await audience.RightsForMany([.. filed.Select(f => f.Reference.TargetDocumentId).Distinct()], cancellationToken);
            bool Sees(Guid id) => targetRights.TryGetValue(id, out var r) && r.CanSee;
            // A reference whose target the caller may not see is left out, as the online listing leaves it out (ADR 0765;
            // owner decision 2026-10-22).
            references.AddRange(filed.Where(f => Sees(f.Reference.TargetDocumentId))
                .Select(f => new FollowedReference(f.Reference, f.TargetName ?? string.Empty, !f.TargetHasVersions)));

            // The targets to follow now: visible, not yet held, each once, within the bound.
            var next = new List<Guid>();
            foreach (var id in filed.Select(f => f.Reference.TargetDocumentId).Distinct().Where(id => Sees(id) && !held.Contains(id)))
            {
                if (targets.Count >= MaxFollowedTargets)
                {
                    boundReached = true;
                    break;
                }

                targets.Add(id);
                held.Add(id);
                rights[id] = targetRights[id];
                next.Add(id);
            }

            if (next.Count == 0)
            {
                break;
            }

            // Each target's subtree, cut to what the caller may see (a child only beneath a visible parent).
            var subtree = await RepositoryExporter.CollectSubtreeAsync(dbContext, next, cancellationToken);
            var beneath = subtree.Where(d => !next.Contains(d.Id) && !held.Contains(d.Id)).ToList();
            var beneathRights = beneath.Count == 0 ? new Dictionary<Guid, EffectiveRights>() : await audience.RightsForMany([.. beneath.Select(d => d.Id)], cancellationToken);
            var visible = new HashSet<Guid>(next);
            var added = new List<Document>();
            foreach (var document in subtree)
            {
                if (next.Contains(document.Id))
                {
                    added.Add(document);
                }
                else if (!held.Contains(document.Id) && visible.Contains(document.ParentId ?? Guid.Empty)
                    && beneathRights.TryGetValue(document.Id, out var r) && r.CanSee)
                {
                    visible.Add(document.Id);
                    held.Add(document.Id);
                    rights[document.Id] = r;
                    added.Add(document);
                }
            }

            broughtIn.AddRange(added);
            frontier = [.. added.Select(d => d.Id)];
        }

        if (boundReached)
        {
            logger.LogWarning(
                "The offline export of {AnchorId} followed {Count} references, its limit; further references are listed but their targets are not brought into the copy",
                anchorId, MaxFollowedTargets);
        }

        var chains = await VisibleChainsAsync(broughtIn.Where(d => targets.Contains(d.Id)), held, [.. chain.Select(c => c.Id)], audience, cancellationToken);
        return new Result(broughtIn, chains, targets, rights, references);
    }

    // The visible folders above the targets, nearest first per level, up to the first one the caller may not see or
    // one the export already holds. One query and one rights batch per level, for all targets together.
    private async Task<List<Document>> VisibleChainsAsync(IEnumerable<Document> targets, IReadOnlySet<Guid> held, HashSet<Guid> known,
        OfflineExportAudience audience, CancellationToken cancellationToken)
    {
        var chains = new List<Document>();
        var parents = targets.Select(t => t.ParentId).OfType<Guid>().Where(p => !held.Contains(p) && !known.Contains(p)).ToHashSet();
        for (var depth = 0; parents.Count > 0 && depth < 256; depth++)
        {
            var level = await dbContext.Documents.Where(d => parents.Contains(d.Id)).ToListAsync(cancellationToken);
            var levelRights = await audience.RightsForMany([.. level.Select(d => d.Id)], cancellationToken);
            var seen = level.Where(d => levelRights.TryGetValue(d.Id, out var r) && r.CanSee).ToList();
            chains.AddRange(seen);
            known.UnionWith(seen.Select(d => d.Id));
            parents = seen.Select(d => d.ParentId).OfType<Guid>().Where(p => !held.Contains(p) && !known.Contains(p)).ToHashSet();
        }

        return chains;
    }
}
