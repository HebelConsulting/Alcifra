using Alcifra.PdfAnnotations;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Reader = UglyToad.PdfPig;

namespace Alcifra.UnitTests;

// ADR 1106: the one PDF writer — marks go in as real annotations any capable viewer can show or hide, the page
// content is untouched, and a parser we already trust reads every one of them back.
public sealed class PdfAnnotationsTests
{
    [Fact]
    public void Every_shape_becomes_a_real_annotation_a_parser_reads_back()
    {
        var marks = new List<PdfMark>
        {
            Mark(PdfMarkShape.Note, x: 0.5, y: 0.5),
            Mark(PdfMarkShape.Highlight, x: 0.1, y: 0.1, w: 0.3, h: 0.05),
            Mark(PdfMarkShape.Box, x: 0.2, y: 0.2, w: 0.3, h: 0.2),
            Mark(PdfMarkShape.Strike, x: 0.2, y: 0.5, w: 0.4, h: 0.03),
            Mark(PdfMarkShape.Arrow, x: 0.1, y: 0.6, w: 0.3, h: 0.2),
            Mark(PdfMarkShape.TextBox, x: 0.3, y: 0.8, w: 0.4, h: 0.08, text: "APPROVED"),
            Mark(PdfMarkShape.Stroke, x: 0.1, y: 0.3, points: [(0.1, 0.3), (0.2, 0.4), (0.3, 0.35)]),
        };

        var annotated = AnnotatedPdf.Write(Page(), marks);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        var annotations = pdf.GetPage(1).GetAnnotations().ToList();
        Assert.Equal(7, annotations.Count);
        Assert.Equal(
            ["Text", "Highlight", "Square", "StrikeOut", "Line", "FreeText", "Ink"],
            annotations.Select(a => a.Type.ToString()).ToArray());
    }

    [Fact]
    public void Coordinates_are_the_stored_normalized_box_on_an_unrotated_page()
    {
        var annotated = AnnotatedPdf.Write(Page(), [Mark(PdfMarkShape.Box, x: 0.1, y: 0.2, w: 0.2, h: 0.2)]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        var rectangle = pdf.GetPage(1).GetAnnotations().Single().Rectangle;

        // A4 portrait: x 0.1..0.3 of 595 = 59.5..178.5; bottom-up y (1-0.4)..(1-0.2) of 842 = 505.2..673.6.
        Assert.Equal(59.5, rectangle.Left, 1);
        Assert.Equal(178.5, rectangle.Right, 1);
        Assert.Equal(505.2, rectangle.Bottom, 1);
        Assert.Equal(673.6, rectangle.Top, 1);
    }

    [Fact]
    public void A_rotated_page_places_the_mark_where_the_viewer_shows_it()
    {
        var annotated = AnnotatedPdf.Write(Page(rotation: 90), [Mark(PdfMarkShape.Box, x: 0.1, y: 0.2, w: 0.2, h: 0.2)]);

        // The raw /Rect, not a parser's view: a reader (PdfPig included) may hand rectangles back through the page's
        // display rotation with inverted bounds, which is no oracle for where the mark sits in user space.
        using var check = PdfReader.Open(new MemoryStream(annotated), PdfDocumentOpenMode.Import);
        var rectangle = check.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!.Elements.GetRectangle("/Rect");

        // /Rotate 90: display x = user y, display y = user x — x 0.2..0.4 of 595 = 119..238, y 0.1..0.3 of 842.
        Assert.Equal(119.0, rectangle.X1, 1);
        Assert.Equal(238.0, rectangle.X2, 1);
        Assert.Equal(84.2, rectangle.Y1, 1);
        Assert.Equal(252.6, rectangle.Y2, 1);
    }

    [Fact]
    public void The_page_count_and_the_existing_annotations_survive()
    {
        var once = AnnotatedPdf.Write(Page(pages: 2), [Mark(PdfMarkShape.Note, x: 0.5, y: 0.5)]);
        var twice = AnnotatedPdf.Write(once, [Mark(PdfMarkShape.Note, x: 0.4, y: 0.4)]);

        using var pdf = Reader.PdfDocument.Open(twice, new Reader.ParsingOptions());
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Equal(2, pdf.GetPage(1).GetAnnotations().Count());
        Assert.Empty(pdf.GetPage(2).GetAnnotations());
    }

    [Fact]
    public void A_mark_on_a_page_the_pdf_does_not_have_is_refused_by_name()
    {
        var mark = new PdfMark(PdfMarkShape.Note, Page: 4, 0.5, 0.5, 0, 0, [], "text", "#FFEB3B", "Author");

        var refusal = Assert.Throws<PdfEmbeddingException>(() => AnnotatedPdf.Write(Page(), [mark]));
        Assert.Contains("page 5", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bytes_that_are_not_a_pdf_are_refused_by_name()
    {
        var refusal = Assert.Throws<PdfEmbeddingException>(() =>
            AnnotatedPdf.Write("not a pdf at all"u8.ToArray(), [Mark(PdfMarkShape.Note, x: 0.5, y: 0.5)]));
        Assert.Contains("could not be opened", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flattened_signature_burns_its_ink_and_name_into_the_page()
    {
        // ADR 1106: flattening is what makes a signature BIND — no annotation exists for a viewer to remove, and the
        // typed name is drawn with the package's own font, so it survives as part of the page.
        var annotated = AnnotatedPdf.WriteFlattened(Page(), [Mark(PdfMarkShape.Signature, x: 0.2, y: 0.4,
            points: [(0.2, 0.4), (0.26, 0.46), (0.32, 0.43)], text: "Anna Meier")]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        var page = pdf.GetPage(1);
        Assert.Empty(page.GetAnnotations());
        Assert.Contains("Anna Meier", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flattened_caption_keeps_its_text_layer()
    {
        // A flattened stamp is page graphics, not pixels: the text stays extractable and searchable.
        var annotated = AnnotatedPdf.WriteFlattened(Page(), [Mark(PdfMarkShape.TextBox, x: 0.3, y: 0.8, w: 0.4, h: 0.08, text: "APPROVED")]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        Assert.Empty(pdf.GetPage(1).GetAnnotations());
        Assert.Contains("APPROVED", pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_faces_split_by_coverage()
    {
        // #2451: the primary is Latin/Greek/Cyrillic; the check mark is the symbols face's, so the writer knows which
        // face to ask before it splits a caption. (The symbols face carries ASCII too — it is a notation font — so a
        // letter proves nothing; Ω is one only the primary has.)
        Assert.True(EmbeddedFont.CoverageOf(EmbeddedFont.Family).Covers('Ω'));
        Assert.False(EmbeddedFont.CoverageOf(EmbeddedFont.Family).Covers('✓'));
        Assert.False(EmbeddedFont.CoverageOf(EmbeddedFont.SymbolsFamily).Covers('Ω'));
        Assert.True(EmbeddedFont.CoverageOf(EmbeddedFont.SymbolsFamily).Covers('✓'));
    }

    [Fact]
    public void A_flattened_caption_draws_a_check_mark_from_the_symbols_face()
    {
        // #2451: "Six panes, as in the revised offer ✓" used to bake the ✓ as a missing-glyph box, because the primary
        // face has no U+2713. The caption is drawn in two runs; the character survives as text in the page.
        var annotated = AnnotatedPdf.WriteFlattened(Page(), [Mark(PdfMarkShape.TextBox, x: 0.2, y: 0.7, w: 0.6, h: 0.08,
            text: "Six panes ✓ reworked")]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        var text = pdf.GetPage(1).Text;
        Assert.Contains("Six panes", text, StringComparison.Ordinal);
        Assert.Contains("✓", text, StringComparison.Ordinal);
        Assert.Contains("reworked", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_on_the_annotation_path_is_ink_that_carries_its_name()
    {
        var annotated = AnnotatedPdf.Write(Page(), [Mark(PdfMarkShape.Signature, x: 0.2, y: 0.4,
            points: [(0.2, 0.4), (0.3, 0.5)], text: "Anna Meier")]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        var annotation = Assert.Single(pdf.GetPage(1).GetAnnotations());
        Assert.Equal("Ink", annotation.Type.ToString());
        Assert.Equal("Anna Meier", annotation.Content);
    }

    [Fact]
    public void A_multi_stroke_drawing_is_several_ink_paths_of_one_annotation()
    {
        // #2460: one drawing = ONE annotation whose /InkList carries every line — a signature's several letters
        // included, since the phone's signature is a drawing session with a name.
        var mark = Mark(PdfMarkShape.Signature, x: 0.2, y: 0.4, text: "Anna Meier") with
        {
            Strokes = [[(0.2, 0.4), (0.3, 0.5)], [(0.4, 0.6), (0.5, 0.4), (0.6, 0.5)]],
        };
        var annotated = AnnotatedPdf.Write(Page(), [mark]);

        using var check = PdfReader.Open(new MemoryStream(annotated), PdfDocumentOpenMode.Import);
        var annotation = check.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!;
        Assert.Equal("/Ink", annotation.Elements.GetName("/Subtype"));
        Assert.Equal("Anna Meier", annotation.Elements.GetString("/Contents"));
        Assert.Equal(2, annotation.Elements.GetArray("/InkList")!.Elements.Count);
    }

    [Fact]
    public void A_multi_stroke_signature_flattens_every_line_and_its_name()
    {
        var mark = Mark(PdfMarkShape.Signature, x: 0.2, y: 0.4, text: "Anna Meier") with
        {
            Strokes = [[(0.2, 0.4), (0.3, 0.5)], [(0.4, 0.6), (0.5, 0.4), (0.6, 0.5)]],
        };
        var annotated = AnnotatedPdf.WriteFlattened(Page(), [mark]);

        using var pdf = Reader.PdfDocument.Open(annotated, new Reader.ParsingOptions());
        Assert.Empty(pdf.GetPage(1).GetAnnotations());
        Assert.Contains("Anna Meier", pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    private static PdfMark Mark(PdfMarkShape shape, double x, double y, double w = 0, double h = 0, string text = "",
        IReadOnlyList<(double X, double Y)>? points = null) =>
        new(shape, Page: 0, x, y, w, h, points ?? [], text, "#FFEB3B", "Demo Admin");

    // A base PDF without text: drawing a string would need a font resolver, and this writer adds no fonts.
    private static byte[] Page(int pages = 1, int rotation = 0)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            page.Rotate = rotation;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.LightGray, new XRect(40, 40, 515, 60));
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }
}
