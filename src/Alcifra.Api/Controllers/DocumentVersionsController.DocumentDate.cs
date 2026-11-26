using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Documents;
using Alcifra.Application.Abstractions;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The version's ISSUING DATE — the PUT sub-resource of <see cref="DocumentVersionsController"/>, split out by
/// responsibility so the controller's file stays under the 1000-line ceiling (#2481, ADR 1110). Same class, same
/// route and authorization: the attributes stay on the main declaration.
/// </summary>
public partial class DocumentVersionsController
{
    // Edits a version's issuing date. Gated on CanEditIndexData (metadata, like the
    // mask/index-data sub-resources) and enqueues a reindex so search reflects the new date. See ADR
    // "System-field search (creator/dates + document date)".
    [HttpPut("{versionId:guid}/document-date")]
    public async Task<IActionResult> SetDocumentDate(Guid documentId, Guid versionId, [FromBody] SetDocumentDateRequest request, CancellationToken cancellationToken)
    {
        var version = await _dbContext.DocumentVersions.SingleOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId, cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        if (!await CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        await EnsureNotFrozenAsync(documentId, cancellationToken);
        await EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);

        if (!DateOnly.TryParse(request.DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidDocumentDateException($"'{request.DocumentDate}' is not a valid date (expected yyyy-MM-dd).");
        }

        // Through the DOCUMENT's contract, not a token of the version's own (#1220): a version is a child
        // guarded by a tracked parent, the editor holds the DOCUMENT's ETag, and a per-version token would put
        // TWO preconditions on one user action (ADR 0794). touchEntity is what makes it bite — EF checks a
        // token only on rows it updates, so a child-only write is otherwise gated by nothing (#1167). This PUT
        // used to SaveChanges raw, so two people setting a document's date silently reverted each other; the
        // pencil path was already safe via 0794's combined PUT .../detail.
        var document = await _dbContext.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        var time = ParseOptionalDocumentTime(request.DocumentTime);

        // A REAL correction — the pair actually changed — leaves the core's note on the version (#2472, ADR 1110),
        // BESIDE the uploader's comment, which is why SystemComment is a column of its own. Setting the same pair
        // again is not a correction and leaves every note untouched.
        var corrected = version.DocumentDate != date || version.DocumentTime != time;
        await _documents.MutateAsync(Request, document,
            apply: () =>
            {
                version.DocumentDate = date;
                version.DocumentTime = time;
                if (corrected)
                {
                    version.SystemComment = Alcifra.Presentation.VersionComments.DateCorrectedToken;
                }

                return Task.CompletedTask;
            },
            afterCommit: async () =>
            {
                await _queue.EnqueueAsync(documentId, cancellationToken);
                await _wormLock.ReconcileAsync(documentId, cancellationToken); // the retention anchor moved
            },
            cancellationToken: cancellationToken);

        var row = new VersionRow(versionId, documentId, version.Status, version.VersionNumber, version.ObjectKey, version.Sha256Hash, version.CreatedAt, version.DocumentDate, version.DocumentTime, version.CreatedByUserId, version.CreatedByServiceAccountId, version.OcrLanguages, version.Comment, version.SystemComment, version.OcrVerdict, version.IsSigned, SizeBytes: version.SizeBytes);
        var documentName = await LoadDocumentNameAsync(documentId, cancellationToken);
        var stamp = version.DocumentTime is { } t ? $"{date:yyyy-MM-dd} {t:HH:mm} UTC" : $"{date:yyyy-MM-dd}";
        await _audit.RecordAsync(AuditActions.DocumentDateChanged, "Document", documentId, documentName, $"Document date set to {stamp} (version {version.VersionNumber})", cancellationToken: cancellationToken);
        return Ok(await _versionResources.BuildAsync(row, documentName, cancellationToken));
    }

    public class SetDocumentDateRequest
    {
        public string DocumentDate { get; set; } = string.Empty;

        // Optional UTC time-of-day ("HH:mm"); null or empty CLEARS it (the "delete the time" action). The
        // client normalizes typed input (0950 / 9:50 / 09:50) to HH:mm before sending (ADR "Optional time...").
        public string? DocumentTime { get; set; }
    }

    // Null/empty means "no time" (date-only); otherwise the canonical wire form is HH:mm (24h, UTC).
    private static TimeOnly? ParseOptionalDocumentTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new InvalidDocumentTimeException($"'{value}' is not a valid time (expected HH:mm, 24-hour UTC).");
        }

        return time;
    }
}
