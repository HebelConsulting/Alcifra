using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace Alcifra.Infrastructure.Modules;

/// <summary>What is referenced in a folder (ABI 1.29, ADR 1046), the other half of a folder beside its children.</summary>
public sealed partial class ModuleArchiveFacade
{
    public async Task<IReadOnlyList<ModuleDocument>> GetReferencedAsync(Guid folderId, Guid maskId, CancellationToken cancellationToken = default)
    {
        // Ordered in memory, for the reason GetChildrenAsync gives: SQLite cannot ORDER BY a DateTimeOffset.
        var rows = (await (
                from r in _dbContext.DocumentReferences
                where r.ParentFolderId == folderId
                join d in _dbContext.Documents on r.TargetDocumentId equals d.Id
                join v in _dbContext.MaskVersions on d.MaskVersionId equals v.Id
                where v.MaskId == maskId
                select new { d.Id, d.ParentId, d.Name, r.CreatedAt, ReferenceId = r.Id })
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.ReferenceId)
            .ToList();

        var ids = rows.Select(r => r.Id).ToList();
        var visibility = await ModuleVisibilityAsync(ids, cancellationToken);
        var principals = await RepresentsUserIdsAsync(ids, cancellationToken);
        var documents = new List<ModuleDocument>(rows.Count);
        foreach (var row in rows)
        {
            if (visibility is not null && !(visibility.TryGetValue(row.Id, out var r) && r.CanSee))
            {
                continue;
            }

            var fields = await FieldsOfAsync(row.Id, cancellationToken);
            documents.Add(new ModuleDocument(row.Id, row.ParentId, row.Name, maskId, fields.Joined)
            {
                FieldLists = fields.Lists,
                RepresentsUserId = principals.TryGetValue(row.Id, out var principal) ? principal : null,
                ReferencedAt = row.CreatedAt,
            });
        }

        return documents;
    }

    public async Task<string?> GetDocumentContentExtensionAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (!await ModuleMaySeeAsync(documentId, cancellationToken))
        {
            return null;
        }

        var pointer = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.CurrentVersionId)
            .SingleOrDefaultAsync(cancellationToken);
        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, pointer, cancellationToken);
        return version is null ? null : Path.GetExtension(version.ObjectKey).TrimStart('.').ToLowerInvariant();
    }
}
