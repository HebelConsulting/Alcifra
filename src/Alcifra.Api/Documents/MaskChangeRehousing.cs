using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Documents;

/// <summary>
/// Re-keys a document's versions when a mask change crosses a content SEGMENT boundary (#2304, ADR 0937): a document
/// given a segmented mask (or losing one) must move its bytes, or the segment's promise — one prefix per module,
/// delete-by-prefix, a separate backup, and for a sealed mask the storage door that knows content only by its key —
/// silently stops holding for that document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The object is copied first, the row follows, the OLD object is deleted last</b> — and the delete runs only after
/// the caller's transaction committed (<see cref="DeleteMovedAsync"/>). A crash between steps leaves a copy nobody
/// reads (sweepable) rather than a row pointing at absent bytes, which is a document that opens to an error.
/// </para>
/// <para>
/// <b>Moving across a segment is content-agnostic by construction.</b> A sealed mask's bytes are copied unopened; the
/// core never parses what it cannot read.
/// </para>
/// </remarks>
public sealed class MaskChangeRehousing(
    AlcifraDbContext dbContext,
    IModuleMaskDeclarations declarations,
    IObjectStorageClient storage,
    ILogger<MaskChangeRehousing> logger)
{
    /// <summary>
    /// Copies and re-points every version whose key the mask change moves, inside the caller's transaction; answers the
    /// OLD keys, which <see cref="DeleteMovedAsync"/> removes once that transaction has committed.
    /// </summary>
    public async Task<IReadOnlyList<string>> RehouseAsync(
        Document document, Guid? fromMaskVersionId, CancellationToken cancellationToken)
    {
        var from = await ContentSegmentKeys.SegmentOfAsync(dbContext, declarations, fromMaskVersionId, cancellationToken);
        var to = await ContentSegmentKeys.SegmentOfAsync(dbContext, declarations, document.MaskVersionId, cancellationToken);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return [];
        }

        var versions = await dbContext.DocumentVersions
            .Where(v => v.DocumentId == document.Id)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        var oldKeys = new List<string>();
        foreach (var version in versions)
        {
            var extension = Path.GetExtension(version.ObjectKey);
            var newKey = ContentSegmentKeys.ContentKey(
                document.TenantId, to, document.StorageFolderId, version.Id, version.CreatedAt, extension);
            if (string.Equals(newKey, version.ObjectKey, StringComparison.Ordinal))
            {
                continue;
            }

            // Object first, row second — DocumentMover's ordering, for its reason: bytes with no row are an orphan a
            // sweep finds; a row pointing at absent bytes is a document that opens to an error.
            logger.LogTrace(
                "Re-keying version {VersionId} of document {DocumentId} across a content segment: {SourceKey} → {DestinationKey}",
                version.Id, document.Id, version.ObjectKey, newKey);
            await storage.CopyObjectAsync(version.ObjectKey, newKey, cancellationToken);
            oldKeys.Add(version.ObjectKey);
            version.ObjectKey = newKey;
        }

        if (oldKeys.Count > 0)
        {
            logger.LogInformation(
                "Document {DocumentId} changed to a mask under segment {ToSegment} (was {FromSegment}): {Count} version(s) "
                + "re-keyed; the old objects are removed once the change commits",
                document.Id, to ?? "(archive)", from ?? "(archive)", oldKeys.Count);
        }

        return oldKeys;
    }

    /// <summary>
    /// Removes the copies the move left behind — AFTER the caller's commit, never inside it, so a rolled-back change
    /// still has its bytes where its rows say. A failed delete is a Warning: the change itself stands, and the old
    /// object is an orphan, not a broken document.
    /// </summary>
    public async Task DeleteMovedAsync(Document document, IReadOnlyList<string> oldKeys, CancellationToken cancellationToken)
    {
        foreach (var key in oldKeys)
        {
            try
            {
                await storage.DeleteObjectAsync(key, cancellationToken);
                logger.LogDebug("Removed {ObjectKey}, the copy a mask change on document {DocumentId} left behind", key, document.Id);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                logger.LogWarning(e,
                    "A mask change on document {DocumentId} re-keyed its content but the old object {ObjectKey} could not "
                    + "be removed; it is an orphan nothing reads", document.Id, key);
            }
        }
    }
}
