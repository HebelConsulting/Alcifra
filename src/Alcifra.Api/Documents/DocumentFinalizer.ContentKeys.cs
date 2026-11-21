using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Alcifra.Infrastructure.Modules;

namespace Alcifra.Api.Documents;

// The content-segment half of finalization (#2304, ADR 0937): split out of DocumentFinalizer.cs at the 1000-line
// ceiling, and it is a responsibility of its own — one key rule applied where classification has just named the mask,
// not another step in the filing flow. Same class, same fields; see the partial's other files for the flow itself.
public partial class DocumentFinalizer
{
    /// <summary>
    /// Files the version's object under the content segment its mask declares (#2304) when it is not there yet: object
    /// first (a copy), the row's key second, the old object removed after the save — the ordering every key move here
    /// uses, so a crash leaves an unread copy rather than a row pointing at absent bytes.
    /// </summary>
    private async Task EnsureSegmentKeyAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        if (_modules is null || ObjectKeyBuilder.IsModuleStagedContentKey(version.ObjectKey))
        {
            return;
        }

        var document = await _dbContext.Documents.SingleAsync(d => d.Id == version.DocumentId, cancellationToken);
        if (await Alcifra.Infrastructure.Modules.ContentSegmentKeys.SegmentOfAsync(
                _dbContext, _modules, document.MaskVersionId, cancellationToken) is not { } segment)
        {
            return;
        }

        var wanted = Alcifra.Infrastructure.Modules.ContentSegmentKeys.ContentKey(
            version.TenantId, segment, document.StorageFolderId, version.Id, version.CreatedAt, Path.GetExtension(version.ObjectKey));
        if (string.Equals(wanted, version.ObjectKey, StringComparison.Ordinal))
        {
            return;
        }

        var oldKey = version.ObjectKey;
        _logger.LogTrace(
            "Re-keying version {VersionId} of document {DocumentId} under its content segment {Segment}: {SourceKey} → {DestinationKey}",
            version.Id, version.DocumentId, segment, oldKey, wanted);
        await _objectStorageClient.CopyObjectAsync(oldKey, wanted, cancellationToken);
        version.ObjectKey = wanted;
        await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);

        // Unreferenced the moment that save commits; a failed removal is an orphan, not a defect.
        try
        {
            await _objectStorageClient.DeleteObjectAsync(oldKey, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(e,
                "Version {VersionId} of document {DocumentId} moved into its content segment, but the old object {ObjectKey} could not be removed",
                version.Id, version.DocumentId, oldKey);
        }
    }

}
