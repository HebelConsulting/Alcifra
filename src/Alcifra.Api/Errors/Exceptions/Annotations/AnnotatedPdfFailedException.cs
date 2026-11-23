using Alcifra.PdfAnnotations;
using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Annotations;

/// <summary>The writer refused the version — unreadable as a PDF, or a mark names a page it does not have
/// (ADR 1106). The cause is logged; the caller is told the export could not be made.</summary>
public sealed class AnnotatedPdfFailedException(PdfEmbeddingException cause) : AnnotationException(
    "ANNOTATED_PDF_FAILED",
    StatusCodes.Status422UnprocessableEntity,
    cause.Message)
{
    public PdfEmbeddingException Cause { get; } = cause;
}
