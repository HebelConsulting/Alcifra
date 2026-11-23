using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Annotations;

/// <summary>Only a PDF version can be exported carrying real PDF annotations (ADR 1106).</summary>
public sealed class AnnotatedPdfUnsupportedException : AnnotationException
{
    public AnnotatedPdfUnsupportedException()
        : base("ANNOTATED_PDF_UNSUPPORTED", StatusCodes.Status415UnsupportedMediaType,
            "Only a PDF version can be exported with its annotations embedded as PDF annotations.")
    {
    }
}
