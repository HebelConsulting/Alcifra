using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Annotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Alcifra.Api.Controllers;

/// <summary>
/// A version as a PDF carrying its annotations (ADR 1106): the ephemeral export — the bytes are built on demand,
/// streamed to the caller and never stored. Available wherever this tenant's doors serve plaintext (a Strict tenant
/// refuses it, like the text layout, because the export reconstructs the document); the rel is advertised on the
/// version resource under the same predicate.
/// </summary>
[Route("api/documents/{documentId:guid}/versions/{versionId:guid}/annotated-pdf")]
[Authorize]
public class DocumentVersionAnnotatedPdfController(
    DocumentVersionAccess versions,
    StrictEnvelopeDelivery strictEnvelopes,
    AnnotatedPdfExport export) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var exported = await BuildAsync(documentId, versionId, cancellationToken);
        return exported is null ? Forbid() : File(exported.Bytes, "application/pdf", exported.FileName);
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var exported = await BuildAsync(documentId, versionId, cancellationToken);
        if (exported is null)
        {
            return Forbid();
        }

        // The body is not written, but the length a caller would receive is stated (the convention: every GET has
        // a HEAD; the export is cheap enough beside the preview resolution this route already pays).
        Response.ContentLength = exported.Bytes.LongLength;
        return Ok();
    }

    // null = the version does not exist for this caller (404-shaped by the caller), Forbid = it exists but its
    // content is not theirs to read.
    private async Task<ExportedAnnotatedPdf?> BuildAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        if (await versions.LoadForReadAsync(documentId, versionId, cancellationToken) is not { } version)
        {
            return null;
        }

        if (!await versions.CanAccessVersionContentAsync(versionId, documentId, cancellationToken))
        {
            return null;
        }

        // A plaintext door like the text layout (ADR 0865): a tenant whose doors refuse plaintext refuses this one
        // too, and the rel is withheld there rather than advertised and then failing.
        await strictEnvelopes.RefuseIfStrictAsync("annotated-pdf", cancellationToken);

        if (!AnnotatedPdfExport.IsPdf(version.ObjectKey))
        {
            throw new AnnotatedPdfUnsupportedException();
        }

        return await export.BuildAsync(version, cancellationToken);
    }
}
