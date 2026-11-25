using Alcifra.Domain.Documents;   // (kept beside the finalizer's own usings)
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Documents;

/// <summary>
/// The finalizer's duplicate enforcement, split from <see cref="DocumentFinalizer"/> when the file met the 1000-line
/// ceiling (CLAUDE.md): the candidates query behind <see cref="DuplicatePolicy"/> (core #2476, ADR 1109) — the same
/// question DuplicatesController answers (ADRs 0398/0686).
/// </summary>
public partial class DocumentFinalizer
{
    // The same question DuplicatesController answers (ADRs 0398/0686): documents whose LATEST CONFIRMED version is
    // byte-identical, tenant-filtered by the context and hidden where the caller cannot see them.
    private async Task<IReadOnlyList<(Guid Id, string Name)>> FindDuplicateDocumentsAsync(Guid documentId, string sha256Hash, CancellationToken cancellationToken)
    {
        var ids = await _dbContext.DocumentVersions
            .Where(v => v.Status == DocumentVersionStatus.Confirmed && v.Sha256Hash == sha256Hash && v.DocumentId != documentId)
            .Select(v => v.DocumentId)
            .Distinct()
            .Take(10)
            .ToListAsync(cancellationToken);
        return ids.Count == 0
            ? []
            : await _dbContext.Documents
                .Where(d => ids.Contains(d.Id))
                .Select(d => new ValueTuple<Guid, string>(d.Id, d.Name))
                .ToListAsync(cancellationToken);
    }
}
