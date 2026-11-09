using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.PasswordStores;

/// <summary>The plain index data of CI/CD secrets (ADR 1084), read for many rows in one query, as the autofill index is.</summary>
public static class CiCdIndexValues
{
    /// <summary>Each CI/CD secret among <paramref name="documentIds"/> with its four lists; other documents are absent.</summary>
    public static async Task<IReadOnlyDictionary<Guid, CiCdIndex>> LoadAsync(
        AlcifraDbContext dbContext, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        var names = new[]
        {
            WellKnownMaskIds.CiCdSecretFields.Targets, WellKnownMaskIds.CiCdSecretFields.SecretKeys,
            WellKnownMaskIds.CiCdSecretFields.FileKeys, WellKnownMaskIds.CiCdSecretFields.Variables,
        };
        var rows = await (
            from v in dbContext.FieldValues
            join f in dbContext.FieldDefinitions on v.FieldDefinitionId equals f.Id
            join m in dbContext.MaskVersions on f.MaskVersionId equals m.Id
            where documentIds.Contains(v.DocumentId) && m.MaskId == WellKnownMaskIds.CiCdSecret && names.Contains(f.Name)
            select new { v.DocumentId, f.Name, v.Value, v.Ordinal })
            .ToListAsync(cancellationToken);

        var secretIds = await dbContext.Documents
            .Where(d => documentIds.Contains(d.Id)
                && dbContext.MaskVersions.Any(m => m.Id == d.MaskVersionId && m.MaskId == WellKnownMaskIds.CiCdSecret))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        // The newest confirmed version of each: when its values (or its sealing) last changed.
        // Grouped here rather than in SQL: a Max over a DateTimeOffset does not translate on SQLite (the model stays
        // provider-agnostic), and a store holds few versions, the superseded ones living a day at most.
        var changed = (await dbContext.DocumentVersions
                .Where(v => secretIds.Contains(v.DocumentId) && v.Status == Domain.Documents.DocumentVersionStatus.Confirmed)
                .Select(v => new { v.DocumentId, v.CreatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(v => v.DocumentId)
            .ToDictionary(g => g.Key, g => g.Max(v => v.CreatedAt));

        return secretIds.ToDictionary(id => id, id =>
        {
            List<string> List(string name) => [.. rows.Where(r => r.DocumentId == id && r.Name == name).OrderBy(r => r.Ordinal).Select(r => r.Value)];
            return new CiCdIndex
            {
                Targets = List(WellKnownMaskIds.CiCdSecretFields.Targets),
                SecretKeys = List(WellKnownMaskIds.CiCdSecretFields.SecretKeys),
                FileKeys = List(WellKnownMaskIds.CiCdSecretFields.FileKeys),
                Variables = List(WellKnownMaskIds.CiCdSecretFields.Variables),
                ChangedAt = changed.TryGetValue(id, out var at) ? at : null,
            };
        });
    }
}
