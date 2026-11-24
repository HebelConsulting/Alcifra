using System.Globalization;
using Alcifra.Api.Errors.Exceptions.Annotations;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Persistence;
using Alcifra.PdfAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Documents;

/// <summary>The bytes and suggested file name of a version exported with its annotations embedded (ADR 1106).</summary>
public sealed record ExportedAnnotatedPdf(byte[] Bytes, string FileName);

/// <summary>
/// Builds a version as a PDF carrying its annotations as REAL PDF annotations (ADR 1106): reads the version's
/// bytes, maps every annotation row of that version to the writer's own shape, and asks
/// <see cref="AnnotatedPdf"/> to append them. Nothing is stored — the caller streams the result (the ephemeral
/// export). Access is decided by the CALLER (the controller), since that is a door question like any other.
/// </summary>
public sealed class AnnotatedPdfExport(
    AlcifraDbContext dbContext,
    IObjectStorageClient storage,
    ILogger<AnnotatedPdfExport> logger)
{
    /// <summary>The export is built in full in memory, so a version past this size is refused rather than
    /// risking the process. Generous for scanned documents; raise it beside a streaming writer, not alone.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    public async Task<ExportedAnnotatedPdf> BuildAsync(VersionRow version, CancellationToken cancellationToken)
    {
        if (!IsPdf(version.ObjectKey))
        {
            throw new AnnotatedPdfUnsupportedException();
        }

        if (version.SizeBytes is { } sizeBytes && sizeBytes > MaxBytes)
        {
            throw new AnnotatedPdfTooLargeException(sizeBytes, MaxBytes);
        }

        // Declared as a CLIENT-SERVING read (ADR 0829): the export reconstructs the document, so the storage seam
        // refuses it on a strict tenant exactly as this route's own guard does — defence in depth, not a second
        // opinion.
        byte[] pdf;
        await using (var source = await storage.GetObjectForClientAsync(version.ObjectKey, "annotated-pdf", cancellationToken))
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            pdf = buffer.ToArray();
        }

        var marks = await MarksAsync(version, cancellationToken);
        try
        {
            var annotated = AnnotatedPdf.Write(pdf, marks);
            return new ExportedAnnotatedPdf(annotated, await FileNameAsync(version, cancellationToken));
        }
        catch (PdfEmbeddingException e)
        {
            logger.LogWarning(e, "The annotated PDF export failed for version {VersionId}", version.Id);
            throw new AnnotatedPdfFailedException(e);
        }
    }

    /// <summary>Whether a version's own content is a PDF — the one format whose export carries real PDF
    /// annotations. (Image and office previews are a later slice of ADR 1106.)</summary>
    public static bool IsPdf(string objectKey) =>
        string.Equals(Path.GetExtension(objectKey), ".pdf", StringComparison.OrdinalIgnoreCase);

    // Every annotation row of the version, in creation order, with its author's name resolved by two batched
    // lookups — the same order the readers list them in.
    private async Task<IReadOnlyList<PdfMark>> MarksAsync(VersionRow version, CancellationToken cancellationToken)
    {
        var rows = await dbContext.DocumentAnnotations
            .Where(a => a.DocumentVersionId == version.Id)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Kind,
                a.PageIndex,
                a.PositionX,
                a.PositionY,
                a.Width,
                a.Height,
                a.Text,
                a.Color,
                a.Points,
                a.CreatedByUserId,
                a.CreatedByServiceAccountId,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var userIds = rows.Where(r => r.CreatedByUserId is not null).Select(r => r.CreatedByUserId!.Value).Distinct().ToList();
        var accountIds = rows.Where(r => r.CreatedByServiceAccountId is not null).Select(r => r.CreatedByServiceAccountId!.Value).Distinct().ToList();
        var userNames = await dbContext.Users.Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var accountNames = await dbContext.ServiceAccounts.Where(s => accountIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        return
        [
            .. rows.Select(row => new PdfMark(
                Shape(row.Kind),
                row.PageIndex,
                row.PositionX,
                row.PositionY,
                row.Width ?? 0,
                row.Height ?? 0,
                [],
                row.Text,
                row.Color,
                row.CreatedByUserId is { } userId && userNames.TryGetValue(userId, out var userName) ? userName
                    : row.CreatedByServiceAccountId is { } accountId && accountNames.TryGetValue(accountId, out var accountName) ? accountName
                    : string.Empty,
                Strokes(row.Points))),
        ];
    }

    // The stored kinds (ADR 0525) to the writer's shapes; stamp and text box both draw as a captioned box there.
    private static PdfMarkShape Shape(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => PdfMarkShape.Highlight,
        AnnotationKind.Rectangle => PdfMarkShape.Box,
        AnnotationKind.Arrow => PdfMarkShape.Arrow,
        AnnotationKind.Stamp or AnnotationKind.TextBox => PdfMarkShape.TextBox,
        AnnotationKind.Strikethrough => PdfMarkShape.Strike,
        AnnotationKind.Freehand => PdfMarkShape.Stroke,
        AnnotationKind.Signature => PdfMarkShape.Signature,
        _ => PdfMarkShape.Note,
    };

    // "x,y x,y …" per stroke, strokes separated by ';' (#2460: one drawing carries its several lines) — the writer
    // draws each as its own /InkList path. A malformed pair is skipped rather than failing the export, exactly as
    // the renderers read it.
    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes(string? points) =>
    [
        .. (points ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(stroke => (IReadOnlyList<(double X, double Y)>)
            [
                .. stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split(','))
                    .Where(pair => pair.Length == 2
                        && double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    .Select(pair => (double.Parse(pair[0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture))),
            ])
            .Where(stroke => stroke.Count > 0),
    ];

    // "<name> (annotations).pdf": what the person receives, not the version's internal key.
    private async Task<string> FileNameAsync(VersionRow version, CancellationToken cancellationToken)
    {
        var name = await dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == version.DocumentId)
            .Select(d => d.Name)
            .SingleOrDefaultAsync(cancellationToken);
        return $"{Path.GetFileNameWithoutExtension(name ?? "document")} (annotations).pdf";
    }
}
