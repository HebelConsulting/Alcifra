using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Annotations;

/// <summary>The annotated export is built in full in memory; a version past the ceiling is refused by name rather
/// than risking the process (ADR 1106).</summary>
public sealed class AnnotatedPdfTooLargeException(long sizeBytes, long maxBytes) : AnnotationException(
    "ANNOTATED_PDF_TOO_LARGE",
    StatusCodes.Status413PayloadTooLarge,
    string.Create(CultureInfo.InvariantCulture,
        $"The version is {sizeBytes} bytes; an annotated PDF export is built in memory and is limited to {maxBytes} bytes."))
{
    public long SizeBytes { get; } = sizeBytes;

    public long MaxBytes { get; } = maxBytes;
}
