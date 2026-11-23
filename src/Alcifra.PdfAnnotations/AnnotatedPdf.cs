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
        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new PdfEmbeddingException("The document could not be opened as a PDF to embed its annotations.", e);
        }

        using (document)
        {
            foreach (var mark in marks)
            {
                if (mark.Page < 0 || mark.Page >= document.PageCount)
                {
                    throw new PdfEmbeddingException($"The annotation names page {mark.Page + 1}; the PDF has {document.PageCount}.");
                }

                Add(document.Pages[mark.Page], mark);
            }

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

            case PdfMarkShape.Stroke:
                {
                    annotation.Elements.SetName("/Subtype", "/Ink");
                    var left = mark.Points.Count == 0 ? mark.X : mark.Points.Min(p => p.X);
                    var top = mark.Points.Count == 0 ? mark.Y : mark.Points.Min(p => p.Y);
                    var right = mark.Points.Count == 0 ? mark.X : mark.Points.Max(p => p.X);
                    var bottom = mark.Points.Count == 0 ? mark.Y : mark.Points.Max(p => p.Y);
                    annotation.Elements.SetRectangle("/Rect", Rect(left, top, right, bottom));
                    var list = new PdfArray(page.Owner);
                    var path = new PdfArray(page.Owner);
                    foreach (var (x, y) in mark.Points)
                    {
                        var (ux, uy) = ToUser(x, y);
                        path.Elements.Add(new PdfReal(ux));
                        path.Elements.Add(new PdfReal(uy));
                    }

                    list.Elements.Add(path);
                    annotation.Elements["/InkList"] = list;
                    break;
                }

            default:
                throw new PdfEmbeddingException($"The mark shape {mark.Shape} cannot be embedded.");
        }

        annots.Elements.Add(annotation);
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
