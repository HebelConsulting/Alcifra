using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Alcifra.PdfAnnotations;

/// <summary>
/// The one PDF writer every surface shares (ADR 1106): the bytes of a PDF plus a list of marks, out comes the same
/// PDF carrying them as REAL annotations — sticky notes, highlights, squares, lines, free text and ink — which any
/// capable viewer shows and hides. The page content is untouched; the marks are appended to each page's /Annots.
/// </summary>
public static class AnnotatedPdf
{
    /// <summary>
    /// Embeds <paramref name="marks"/> into <paramref name="pdf"/> and returns the annotated bytes. The input is not
    /// changed; the output is a fresh document (no incremental-update preservation — the ephemeral export and a new
    /// stored version are both new artifacts, never a rewrite of the source version).
    /// </summary>
    /// <exception cref="PdfEmbeddingException">The PDF cannot be opened, or a mark names a page it does not have.</exception>
    public static byte[] Write(byte[] pdf, IReadOnlyList<PdfMark> marks)
    {
        using var document = Open(pdf);
        foreach (var mark in marks)
        {
            if (mark.Page < 0 || mark.Page >= document.PageCount)
            {
                throw new PdfEmbeddingException($"The annotation names page {mark.Page + 1}; the PDF has {document.PageCount}.");
            }

            Add(document.Pages[mark.Page], mark);
        }

        return Save(document);
    }

    // One annotation dictionary, appended to the page's own /Annots array (a page that already carries annotations
    // keeps them). Coordinates are normalized and top-left based (core ADR 0525); the page's rotation is applied.
    private static void Add(PdfPage page, PdfMark mark)
    {
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(page.Owner);
            page.Elements["/Annots"] = annots;
        }

        // The MEDIA BOX, not page.Width/Height: PDFsharp presents those already swapped by /Rotate, and this mapping
        // applies the rotation itself (found by the rotated-page test). The box's origin is honoured.
        var media = page.MediaBox;
        double width = media.X2 - media.X1;
        double height = media.Y2 - media.Y1;
        var rotation = ((page.Rotate % 360) + 360) % 360;

        (double X, double Y) ToUser(double normalizedX, double normalizedY) => rotation switch
        {
            90 => (media.X1 + normalizedY * width, media.Y1 + normalizedX * height),
            180 => (media.X1 + (1 - normalizedX) * width, media.Y1 + normalizedY * height),
            270 => (media.X1 + (1 - normalizedY) * width, media.Y1 + (1 - normalizedX) * height),
            _ => (media.X1 + normalizedX * width, media.Y1 + (1 - normalizedY) * height),
        };

        PdfRectangle Rect(double left, double top, double right, double bottom)
        {
            var (ax, ay) = ToUser(left, top);
            var (bx, by) = ToUser(right, bottom);
            var (cx, cy) = ToUser(right, top);
            var (dx, dy) = ToUser(left, bottom);
            var xs = new[] { ax, bx, cx, dx };
            var ys = new[] { ay, by, cy, dy };
            return new PdfRectangle(new XRect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()));
        }

        var annotation = new PdfDictionary(page.Owner);
        annotation.Elements.SetName("/Type", "/Annot");
        annotation.Elements.SetInteger("/F", 4);   // print: the export is a document, not a screen note
        annotation.Elements.SetString("/T", mark.Author);
        annotation.Elements.SetString("/NM", Guid.NewGuid().ToString("N"));
        annotation.Elements.SetString("/M", "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z");
        SetColour(annotation, "/C", mark.Colour);

        switch (mark.Shape)
        {
            case PdfMarkShape.Note:
                {
                    annotation.Elements.SetName("/Subtype", "/Text");
                    annotation.Elements.SetName("/Name", "/Note");
                    annotation.Elements.SetString("/Contents", mark.Text);
                    annotation.Elements.SetRectangle("/Rect", Rect(Math.Max(0, mark.X - 0.012), Math.Max(0, mark.Y - 0.012),
                        Math.Min(1, mark.X + 0.012), Math.Min(1, mark.Y + 0.012)));
                    break;
                }

            case PdfMarkShape.Highlight:
                {
                    annotation.Elements.SetName("/Subtype", "/Highlight");
                    annotation.Elements.SetString("/Contents", mark.Text);
                    var rect = Rect(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height);
                    annotation.Elements.SetRectangle("/Rect", rect);
                    annotation.Elements["/QuadPoints"] = QuadPoints(page.Owner, ToUser, mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height);
                    annotation.Elements.SetInteger("/CA", 35);   // 0.35: the renderers' own highlight alpha
                    break;
                }

            case PdfMarkShape.Box:
                {
                    annotation.Elements.SetName("/Subtype", "/Square");
                    annotation.Elements.SetString("/Contents", mark.Text);
                    annotation.Elements.SetRectangle("/Rect", Rect(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                    break;
                }

            case PdfMarkShape.Strike:
                {
                    annotation.Elements.SetName("/Subtype", "/StrikeOut");
                    annotation.Elements.SetRectangle("/Rect", Rect(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                    annotation.Elements["/QuadPoints"] = QuadPoints(page.Owner, ToUser, mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height);
                    break;
                }

            case PdfMarkShape.Arrow:
                {
                    annotation.Elements.SetName("/Subtype", "/Line");
                    var (x1, y1) = ToUser(mark.X, mark.Y);
                    var (x2, y2) = ToUser(mark.X + mark.Width, mark.Y + mark.Height);
                    var line = new PdfArray(page.Owner);
                    foreach (var v in new[] { x1, y1, x2, y2 })
                    {
                        line.Elements.Add(new PdfReal(v));
                    }

                    annotation.Elements["/L"] = line;
                    var ends = new PdfArray(page.Owner);
                    ends.Elements.Add(new PdfName("/None"));
                    ends.Elements.Add(new PdfName("/OpenArrow"));
                    annotation.Elements["/LE"] = ends;
                    annotation.Elements.SetRectangle("/Rect", Rect(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                    break;
                }

            case PdfMarkShape.TextBox:
                {
                    annotation.Elements.SetName("/Subtype", "/FreeText");
                    annotation.Elements.SetString("/Contents", mark.Text);
                    annotation.Elements.SetRectangle("/Rect", Rect(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                    // Standard-14 Helvetica: nothing embeds, and every viewer knows it (the caption is data, not a font).
                    annotation.Elements.SetString("/DA", $"/Helv 12 Tf {Colour(mark.Colour)} rg");
                    annotation.Elements.SetInteger("/Q", 1);   // centred, as both clients draw a stamp
                    var border = new PdfDictionary(page.Owner);
                    border.Elements.SetInteger("/W", 1);
                    annotation.Elements["/BS"] = border;
                    break;
                }

            case PdfMarkShape.Signature:
                {
                    // A real-annotation export keeps a signature toggleable, like every mark; only FLATTENING makes it
                    // bind (ADR 1106) — the stored signed version is always the flattened one.
                    annotation.Elements.SetName("/Subtype", "/Ink");
                    annotation.Elements.SetString("/Contents", mark.Text);
                    AddInk(page.Owner, annotation, mark, ToUser);
                    break;
                }

            case PdfMarkShape.Stroke:
                {
                    annotation.Elements.SetName("/Subtype", "/Ink");
                    AddInk(page.Owner, annotation, mark, ToUser);
                    break;
                }

            default:
                throw new PdfEmbeddingException($"The mark shape {mark.Shape} cannot be embedded.");
        }

        annots.Elements.Add(annotation);
    }

    private static PdfDocument Open(byte[] pdf)
    {
        try
        {
            return PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new PdfEmbeddingException("The document could not be opened as a PDF to embed its annotations.", e);
        }
    }

    private static byte[] Save(PdfDocument document)
    {
        try
        {
            using var output = new MemoryStream();
            document.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new PdfEmbeddingException("The annotated PDF could not be written.", e);
        }
    }

    private static void AddInk(PdfDocument owner, PdfDictionary annotation, PdfMark mark,
        Func<double, double, (double X, double Y)> toUser)
    {
        var ink = mark.Ink;
        var all = ink.SelectMany(stroke => stroke).ToList();
        var left = all.Count == 0 ? mark.X : all.Min(p => p.X);
        var top = all.Count == 0 ? mark.Y : all.Min(p => p.Y);
        var right = all.Count == 0 ? mark.X : all.Max(p => p.X);
        var bottom = all.Count == 0 ? mark.Y : all.Max(p => p.Y);
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new XRect(
            Math.Min(toUser(left, top).X, toUser(right, bottom).X), Math.Min(toUser(left, top).Y, toUser(right, bottom).Y),
            Math.Abs(toUser(right, bottom).X - toUser(left, top).X), Math.Abs(toUser(right, bottom).Y - toUser(left, top).Y))));

        // One ink path PER stroke (#2460): /InkList natively holds the several lines of one drawing.
        var list = new PdfArray(owner);
        foreach (var stroke in ink.Where(stroke => stroke.Count > 0))
        {
            var path = new PdfArray(owner);
            foreach (var (x, y) in stroke)
            {
                var (ux, uy) = toUser(x, y);
                path.Elements.Add(new PdfReal(ux));
                path.Elements.Add(new PdfReal(uy));
            }

            list.Elements.Add(path);
        }

        annotation.Elements["/InkList"] = list;
    }

    /// <summary>
    /// Draws the marks into the pages themselves (ADR 1106's flattening): nothing is added to /Annots, so no viewer
    /// can toggle, move or delete a mark — the signature binds. Graphic marks append vector drawing to the page
    /// content (the text layer survives); the embedded font supplies captions and a signature's name.
    /// </summary>
    public static byte[] WriteFlattened(byte[] pdf, IReadOnlyList<PdfMark> marks)
    {
        EmbeddedFont.Register();
        using var document = Open(pdf);
        foreach (var mark in marks)
        {
            if (mark.Page < 0 || mark.Page >= document.PageCount)
            {
                throw new PdfEmbeddingException($"The annotation names page {mark.Page + 1}; the PDF has {document.PageCount}.");
            }

            var page = document.Pages[mark.Page];
            var media = page.MediaBox;
            double width = media.X2 - media.X1;
            double height = media.Y2 - media.Y1;
            var rotation = ((page.Rotate % 360) + 360) % 360;

            // XGraphics draws in the page's own top-left space; the display rotation is applied here so a mark lands
            // where the reader saw it (the same four transforms the /Rect path uses, y down).
            (float X, float Y) At(double nx, double ny) => rotation switch
            {
                90 => ((float)(media.X1 + ny * width), (float)(media.Y1 + height - nx * height)),
                180 => ((float)(media.X1 + (1 - nx) * width), (float)(media.Y1 + (1 - ny) * height)),
                270 => ((float)(media.X1 + (1 - ny) * width), (float)(media.Y1 + nx * height)),
                _ => ((float)(media.X1 + nx * width), (float)(media.Y1 + ny * height)),
            };

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            Draw(gfx, mark, At);
        }

        return Save(document);
    }

    private static void Draw(XGraphics gfx, PdfMark mark, Func<double, double, (float X, float Y)> at)
    {
        var colour = ColourOf(mark.Colour);
        var pen = new XPen(colour, 2) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        XRect Box(double left, double top, double right, double bottom)
        {
            var (ax, ay) = at(left, top);
            var (bx, by) = at(right, bottom);
            return new XRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
        }

        void Polyline()
        {
            // Every stroke of the drawing (#2460): a signature's several letters are several lines, one mark.
            foreach (var stroke in mark.Ink)
            {
                for (var i = 1; i < stroke.Count; i++)
                {
                    var (ax, ay) = at(stroke[i - 1].X, stroke[i - 1].Y);
                    var (bx, by) = at(stroke[i].X, stroke[i].Y);
                    gfx.DrawLine(pen, ax, ay, bx, by);
                }
            }
        }

        switch (mark.Shape)
        {
            case PdfMarkShape.Highlight:
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(90, colour.R, colour.G, colour.B)), Box(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                break;

            case PdfMarkShape.Box:
                gfx.DrawRectangle(pen, Box(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height));
                break;

            case PdfMarkShape.Strike:
                {
                    var (ax, ay) = at(mark.X, mark.Y + mark.Height / 2);
                    var (bx, by) = at(mark.X + mark.Width, mark.Y + mark.Height / 2);
                    gfx.DrawLine(pen, ax, ay, bx, by);
                    break;
                }

            case PdfMarkShape.Arrow:
                {
                    var (ax, ay) = at(mark.X, mark.Y);
                    var (bx, by) = at(mark.X + mark.Width, mark.Y + mark.Height);
                    gfx.DrawLine(pen, ax, ay, bx, by);
                    var angle = Math.Atan2(by - ay, bx - ax);
                    foreach (var side in new[] { -0.45, 0.45 })
                    {
                        gfx.DrawLine(pen, bx, by, bx - (float)(12 * Math.Cos(angle + side)), by - (float)(12 * Math.Sin(angle + side)));
                    }

                    break;
                }

            case PdfMarkShape.TextBox:
                {
                    var box = Box(mark.X, mark.Y, mark.X + mark.Width, mark.Y + mark.Height);
                    gfx.DrawRectangle(pen, box);
                    DrawText(gfx, mark.Text, 12, new XSolidBrush(colour), box, XStringFormats.Center);
                    break;
                }

            case PdfMarkShape.Note:
                {
                    var (cx, cy) = at(mark.X, mark.Y);
                    var marker = new XRect(cx - 8, cy - 8, 16, 16);
                    gfx.DrawRectangle(new XPen(colour, 1.5), marker);
                    if (!string.IsNullOrWhiteSpace(mark.Text))
                    {
                        DrawText(gfx, mark.Text, 10, XBrushes.Black, new XRect(marker.Right + 3, marker.Top - 4, 240, 24),
                            XStringFormats.TopLeft);
                    }

                    break;
                }

            case PdfMarkShape.Stroke:
                Polyline();
                break;

            case PdfMarkShape.Signature:
                {
                    Polyline();
                    if (!string.IsNullOrWhiteSpace(mark.Text))
                    {
                        var all = mark.Ink.SelectMany(stroke => stroke).ToList();
                        var left = all.Count == 0 ? mark.X : all.Min(p => p.X);
                        var top = all.Count == 0 ? mark.Y : all.Min(p => p.Y);
                        var right = all.Count == 0 ? mark.X : all.Max(p => p.X);
                        var bottom = all.Count == 0 ? mark.Y : all.Max(p => p.Y);
                        var ink = Box(left, top, right, bottom);
                        DrawText(gfx, mark.Text, 10, new XSolidBrush(colour),
                            new XRect(ink.Left - 40, ink.Bottom + 2, ink.Width + 80, 18), XStringFormats.Center);
                    }

                    break;
                }
        }
    }

    // A caption in one or two faces (#2451): the characters the primary lacks (✓ U+2713 and its dingbat kind) come
    // from the symbols face, drawn as runs of consecutive characters the SAME face covers, so each run keeps its own
    // kerning and the alignment matches what DrawString would have produced. A character NEITHER face has stays with
    // the primary — the missing-glyph box the writer always drew for it.
    private static void DrawText(XGraphics gfx, string text, double size, XBrush brush, XRect box, XStringFormat format)
    {
        if (string.IsNullOrEmpty(text) || text.All(EmbeddedFont.Covers))
        {
            gfx.DrawString(text, TextFont(size), brush, box, format);
            return;
        }

        var runs = new List<(string Text, bool Symbols)>();
        foreach (var c in text)
        {
            var symbols = !EmbeddedFont.Covers(c) && EmbeddedFont.SymbolsCover(c);
            if (runs.Count > 0 && runs[^1].Symbols == symbols)
            {
                runs[^1] = (runs[^1].Text + c, symbols);
            }
            else
            {
                runs.Add((c.ToString(), symbols));
            }
        }

        var fonts = runs.Select(run => TextFont(size, run.Symbols)).ToList();
        var widths = runs.Select((run, i) => gfx.MeasureString(run.Text, fonts[i]).Width).ToList();
        var height = gfx.MeasureString("Ag", TextFont(size)).Height;
        var x = format.Alignment == XStringAlignment.Center ? box.X + ((box.Width - widths.Sum()) / 2) : box.X;
        var y = format.LineAlignment == XLineAlignment.Center ? box.Y + ((box.Height - height) / 2) : box.Y;
        for (var i = 0; i < runs.Count; i++)
        {
            gfx.DrawString(runs[i].Text, fonts[i], brush, new XRect(x, y, box.Width, box.Height), XStringFormats.TopLeft);
            x += widths[i];
        }
    }

    private static XFont TextFont(double size, bool symbols = false) =>
        new(symbols ? EmbeddedFont.SymbolsFamily : EmbeddedFont.Family, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    private static XColor ColourOf(string colour)
    {
        var (r, g, b) = Components(colour);
        return XColor.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    // The four corners in the spec's order: upper-left, upper-right, lower-left, lower-right — each through the
    // page's rotation, so the quad follows the text it marks on a rotated page too.
    private static PdfArray QuadPoints(PdfDocument document, Func<double, double, (double X, double Y)> toUser,
        double left, double top, double right, double bottom)
    {
        var quad = new PdfArray(document);
        foreach (var (nx, ny) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
        {
            var (ux, uy) = toUser(nx, ny);
            quad.Elements.Add(new PdfReal(ux));
            quad.Elements.Add(new PdfReal(uy));
        }

        return quad;
    }

    private static void SetColour(PdfDictionary annotation, string key, string colour)
    {
        var array = new PdfArray(annotation.Owner);
        var (r, g, b) = Components(colour);
        foreach (var component in new[] { r, g, b })
        {
            array.Elements.Add(new PdfReal(component));
        }

        annotation.Elements[key] = array;
    }

    // "#RRGGBB" to three components; the core's default yellow when the value is not a colour.
    private static (double R, double G, double B) Components(string colour) =>
        colour is { Length: 7 } && colour[0] == '#'
        && int.TryParse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
        && int.TryParse(colour.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
        && int.TryParse(colour.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
            ? (r / 255d, g / 255d, b / 255d)
            : (1, 0.92, 0.23);

    private static string Colour(string colour)
    {
        var (r, g, b) = Components(colour);
        return string.Create(CultureInfo.InvariantCulture, $"{r:0.###} {g:0.###} {b:0.###}");
    }
}

/// <summary>A PDF could not be opened, annotated or written — the caller decides what the user is told.</summary>
public sealed class PdfEmbeddingException(string message, Exception? inner = null) : Exception(message, inner);
