using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.PasswordStores;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Presentation;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// A CI/CD secret's plain index data (ADR 1084), staged in the same transaction as the sealed version it describes, so the
/// key names and the values they name are one change (ADR 0794): a client never sees new names over old values.
/// </summary>
public static class CiCdIndexWriter
{
    /// <summary>
    /// Replaces the four lists with <paramref name="index"/>'s, or does nothing when it is null (a client sending only a
    /// re-seal). Refuses, before anything is staged, a document that is not a CI/CD secret and data the rules refuse.
    /// </summary>
    public static async Task ApplyAsync(AlcifraDbContext dbContext, Guid documentId, CiCdIndex? index, CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return;
        }

        var mask = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (_, v) => new { v.MaskId, MaskVersionId = v.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (mask?.MaskId != WellKnownMaskIds.CiCdSecret)
        {
            throw new NotAPasswordStoreItemException();
        }

        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [WellKnownMaskIds.CiCdSecretFields.Targets] = Clean(index.Targets),
            [WellKnownMaskIds.CiCdSecretFields.SecretKeys] = Clean(index.SecretKeys),
            [WellKnownMaskIds.CiCdSecretFields.FileKeys] = Clean(index.FileKeys),
            [WellKnownMaskIds.CiCdSecretFields.Variables] = index.Variables?.Where(v => v.Length > 0).ToList() ?? [],
        };
        if (CiCdEntryRules.Problems(
            lists[WellKnownMaskIds.CiCdSecretFields.SecretKeys], lists[WellKnownMaskIds.CiCdSecretFields.FileKeys],
            lists[WellKnownMaskIds.CiCdSecretFields.Variables], lists[WellKnownMaskIds.CiCdSecretFields.Targets]) is { Count: > 0 } problems)
        {
            throw new CiCdEntryInvalidException(problems);
        }

        var names = lists.Keys.ToArray();
        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == mask.MaskVersionId && names.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        var definitionIds = definitions.Select(d => d.Id).ToList();
        dbContext.FieldValues.RemoveRange(await dbContext.FieldValues
            .Where(v => v.DocumentId == documentId && definitionIds.Contains(v.FieldDefinitionId))
            .ToListAsync(cancellationToken));

        var tenantId = await dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.TenantId).FirstAsync(cancellationToken);
        foreach (var definition in definitions)
        {
            foreach (var (value, ordinal) in lists[definition.Name].Select((v, i) => (v, i)))
            {
                dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DocumentId = documentId,
                    FieldDefinitionId = definition.Id,
                    Value = value,
                    Ordinal = ordinal,
                });
            }
        }
    }

    // Names and targets are trimmed and empty ones dropped; a variable's value is kept exactly, spaces included.
    private static List<string> Clean(IReadOnlyList<string>? values) =>
        values?.Select(v => v.Trim()).Where(v => v.Length > 0).ToList() ?? [];
}

/// <summary>A CI/CD secret's index data as a client states it with a new version (ADR 1084): each list replaces the stored one.</summary>
public sealed class CiCdIndex
{
    public List<string>? Targets { get; set; }

    public List<string>? SecretKeys { get; set; }

    public List<string>? FileKeys { get; set; }

    /// <summary><c>NAME=value</c>: not secret, so not sealed.</summary>
    public List<string>? Variables { get; set; }

    /// <summary>
    /// When the secret's current version was filed: a target updated before it is stale. Output only; a client sending it
    /// with a version changes nothing.
    /// </summary>
    public DateTimeOffset? ChangedAt { get; set; }
}
