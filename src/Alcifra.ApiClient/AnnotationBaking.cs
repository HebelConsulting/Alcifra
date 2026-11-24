using Alcifra.PdfAnnotations;

namespace Alcifra.ApiClient;

/// <summary>
/// Closing a signing session on any surface (#2453, ADR 1107): a version's annotations as the shared writer's marks,
/// and the flattened bytes of the next version — ONE mapping and ONE writer for the web and the desktop (the phone
/// bakes with the same package; its mapper is the older copy and folds into this when next touched).
/// </summary>
public static class AnnotationBaking
{
    /// <summary>Whether the version carries a signature (kind 8) — the state the completion gate speaks of.</summary>
    public static bool HasSignature(IReadOnlyList<AnnotationsClient.AnnotationInfo> annotations) =>
        annotations.Any(a => a.Kind == 8);

    /// <summary>The version's PDF bytes with every mark drawn into the pages, as the next version's bytes.</summary>
    /// <exception cref="PdfEmbeddingException">The bytes are not a PDF that can be annotated, or the bake failed.</exception>
    public static byte[] Flatten(byte[] pdf, IReadOnlyList<AnnotationsClient.AnnotationInfo> annotations) =>
        AnnotatedPdf.WriteFlattened(pdf, MarksOf(annotations));

    /// <summary>The rows as the writer's marks, in list order — through the writer's ONE stored-kind mapping
    /// (<see cref="PdfMark.FromStored"/>), so the bake and the export cannot disagree.</summary>
    public static IReadOnlyList<PdfMark> MarksOf(IReadOnlyList<AnnotationsClient.AnnotationInfo> annotations) =>
    [
        .. annotations.Select(a => PdfMark.FromStored(a.Kind, a.PageIndex, a.PositionX, a.PositionY, a.Width, a.Height,
            a.Text, a.Color, a.AuthorName, a.Points)),
    ];
}
