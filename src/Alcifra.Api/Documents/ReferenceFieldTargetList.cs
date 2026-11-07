using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>One document a reference field may point at, as a picker offers it.</summary>
public sealed record ReferenceFieldTarget(Guid Id, string Name);

/// <summary>
/// What a reference field's picker offers (#2214, ADR 1068): documents wearing one of the masks the field accepts, narrowed by
/// what was typed, and only those the caller may see. Rights are asked per candidate and the walk is bounded, so a tenant with
/// many documents costs a bounded read and a person who sees few of them still gets a full page where one exists.
/// </summary>
public static class ReferenceFieldTargetList
{
    private const int CandidatesRead = 200;

    public static async Task<IReadOnlyList<ReferenceFieldTarget>> ForAsync(
        AlcifraDbContext db, Guid fieldId, string? typed, int take, Func<Guid, CancellationToken, Task<bool>> canSee, CancellationToken cancellationToken)
    {
        var masks = db.FieldDefinitionAcceptedMasks.Where(a => a.FieldDefinitionId == fieldId).Select(a => a.MaskId);
        var versions = db.MaskVersions.Where(v => masks.Contains(v.MaskId)).Select(v => v.Id);
        var query = db.Documents.Where(d => versions.Contains(d.MaskVersionId));
        if (typed is { Length: > 0 })
        {
            query = query.Where(d => EF.Functions.Like(d.Name, $"%{typed}%"));
        }

        var candidates = await query.OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new ReferenceFieldTarget(d.Id, d.Name))
            .Take(CandidatesRead)
            .ToListAsync(cancellationToken);

        var visible = new List<ReferenceFieldTarget>();
        foreach (var candidate in candidates)
        {
            if (visible.Count == take)
            {
                break;
            }

            if (await canSee(candidate.Id, cancellationToken))
            {
                visible.Add(candidate);
            }
        }

        return visible;
    }
}
