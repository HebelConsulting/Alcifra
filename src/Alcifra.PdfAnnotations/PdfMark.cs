namespace Alcifra.PdfAnnotations;

/// <summary>How a mark is drawn, as this library understands it — its own shape, mapped by each caller from
/// whatever its layer already speaks (the annotation kinds, ADR 0525).</summary>
public enum PdfMarkShape
{
    /// <summary>A sticky note: a marker with its text, opened by the viewer.</summary>
    Note,

    /// <summary>A translucent fill.</summary>
    Highlight,

    /// <summary>An outline.</summary>
    Box,

    /// <summary>A line through a box's middle.</summary>
    Strike,

    /// <summary>A line with a head at its end.</summary>
    Arrow,

    /// <summary>An outline carrying its text (a stamp or a text box).</summary>
    TextBox,

    /// <summary>A pen stroke: a poly-line of normalized points.</summary>
    Stroke,

    /// <summary>A signature (ADR 1106): ink in <see cref="PdfMark.Points"/> plus the signer's typed name in
    /// <see cref="PdfMark.Text"/>, drawn with the name beneath the ink when flattened.</summary>
    Signature,
}

/// <summary>
/// One mark to embed, in the page's NORMALIZED coordinates (0..1, origin top-left — the stored annotation
/// convention, core ADR 0525). The library converts to PDF user space, including a rotated page.
/// </summary>
/// <param name="Width">The box's normalized width; ignored for <see cref="PdfMarkShape.Note"/> and
/// <see cref="PdfMarkShape.Stroke"/>.</param>
/// <param name="Points">A stroke's normalized points; empty for every other shape.</param>
/// <param name="Colour">"#RRGGBB".</param>
/// <param name="Author">Shown by the viewer as the annotation's author.</param>
public sealed record PdfMark(PdfMarkShape Shape, int Page, double X, double Y, double Width, double Height,
    IReadOnlyList<(double X, double Y)> Points, string Text, string Colour, string Author);
