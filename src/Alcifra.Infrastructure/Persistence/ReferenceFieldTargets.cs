using Microsoft.EntityFrameworkCore;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// The one check of a reference field's accepted masks (#2214, ADR 1068): where a field names masks, a value must point at a
/// document wearing one of them, the way an accepted extension refuses the wrong file. Called from SaveChanges, the sole
/// enforcement point. Only a value being written is checked: a target whose mask changes later is the world changing under a
/// correct value, as a target purged later is.
/// </summary>
internal static class ReferenceFieldTargets
{
    public static async Task EnsureAcceptedAsync(
        AlcifraDbContext db, IReadOnlyList<(Guid FieldDefinitionId, string FieldName, Guid Target)> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        var fieldIds = values.Select(v => v.FieldDefinitionId).Distinct().ToList();
        var accepted = (await db.FieldDefinitionAcceptedMasks
                .Where(a => fieldIds.Contains(a.FieldDefinitionId))
                .Select(a => new { a.FieldDefinitionId, a.MaskId })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.FieldDefinitionId, a => a.MaskId);
        var restricted = values.Where(v => accepted[v.FieldDefinitionId].Any()).ToList();
        if (restricted.Count == 0)
        {
            return;
        }

        var targets = restricted.Select(v => v.Target).Distinct().ToList();
        var worn = await db.Documents.Where(d => targets.Contains(d.Id))
            .Select(d => new { d.Id, MaskId = db.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault() })
            .ToDictionaryAsync(d => d.Id, d => d.MaskId, cancellationToken);

        foreach (var value in restricted)
        {
            if (worn.GetValueOrDefault(value.Target) is not { } mask || !accepted[value.FieldDefinitionId].Contains(mask))
            {
                throw new Domain.Masks.FieldValueRejectedException(
                    $"Field value '{value.Target}' for '{value.FieldName}' is not a document of a kind this field accepts.");
            }
        }
    }
}
