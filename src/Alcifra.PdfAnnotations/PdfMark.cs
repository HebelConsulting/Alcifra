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
/// <param name="Strokes">A drawing's lines (#2460): when non-empty it wins over <paramref name="Points"/>, which
/// stays the single-line form every caller used before multi-stroke drawings existed.</param>
public sealed record PdfMark(PdfMarkShape Shape, int Page, double X, double Y, double Width, double Height,
    IReadOnlyList<(double X, double Y)> Points, string Text, string Colour, string Author,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Strokes = null)
{
    /// <summary>The ink as strokes: <see cref="Strokes"/> when a drawing carries several lines, else the single
    /// <see cref="Points"/> line (empty for shapes that are not ink).</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Ink =>
        Strokes is { Count: > 0 } strokes ? strokes : [Points];

    /// <summary>
    /// A mark from a stored annotation row's fields (ADR 0525's kinds; ';'-separated strokes, #2460) — the ONE
    /// mapping from the wire's shape to the writer's, shared by every surface that bakes, so no client keeps its own
    /// copy (a drift here is how the export and the bake come to disagree).
    /// </summary>
    public static PdfMark FromStored(int kind, int page, double x, double y, double? width, double? height,
        string? text, string? colour, string? author, string? points) =>
        new(ShapeOf(kind), page, x, y, width ?? 0, height ?? 0, [], text ?? string.Empty, colour ?? "#000000",
            author ?? string.Empty, StrokesOf(points));

    // The stored kinds (ADR 0525) to the writer's shapes; stamp and text box both draw as a captioned box there.
    private static PdfMarkShape ShapeOf(int kind) => kind switch
    {
        1 => PdfMarkShape.Highlight,
        2 => PdfMarkShape.Box,
        3 => PdfMarkShape.Arrow,
        4 or 6 => PdfMarkShape.TextBox,
        5 => PdfMarkShape.Strike,
        7 => PdfMarkShape.Stroke,
        8 => PdfMarkShape.Signature,
        _ => PdfMarkShape.Note,
    };

    // "x,y x,y …" per stroke, ';' between the lines of one drawing (#2460); a malformed pair is skipped rather than
    // failing the bake, exactly as every renderer reads it.
    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> StrokesOf(string? points) =>
    [
        .. (points ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(stroke => (IReadOnlyList<(double X, double Y)>)
            [
                .. stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split(','))
                    .Where(pair => pair.Length == 2
                        && double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
                        && double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    .Select(pair => (double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture))),
            ])
            .Where(stroke => stroke.Count > 0),
    ];
}
