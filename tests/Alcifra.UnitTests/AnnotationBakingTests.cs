using Alcifra.ApiClient;
using PdfSharp.Pdf;
using Reader = UglyToad.PdfPig;

namespace Alcifra.UnitTests;

// #2453 / ADR 1107: the shared bake every surface closes a signing session with — the rows as the writer's marks
// (a drawing's lines stay several strokes) and the flattened bytes the new version is filed from. The phone's mapper
// asserts the same shape on its side; a drift here would bake differently from the server's export.
public sealed class AnnotationBakingTests
{
    [Fact]
    public void Only_a_signature_arm_the_gate()
    {
        Assert.True(AnnotationBaking.HasSignature([Row(8, text: "Anna Meier", points: "0.2,0.4 0.3,0.5")]));
        Assert.False(AnnotationBaking.HasSignature([Row(7, points: "0.1,0.2 0.3,0.4"), Row(0, text: "note")]));
        Assert.False(AnnotationBaking.HasSignature([]));
    }

    [Fact]
    public void A_drawings_several_lines_stay_several_strokes_for_the_writer()
    {
        var mark = Assert.Single(AnnotationBaking.MarksOf([Row(8, text: "Anna Meier", points: "0.2,0.4 0.3,0.5;0.4,0.6 0.5,0.4 0.6,0.5")]));

        Assert.Equal(2, mark.Ink.Count);
        Assert.Equal([(0.2, 0.4), (0.3, 0.5)], mark.Ink[0]);
        Assert.Equal([(0.4, 0.6), (0.5, 0.4), (0.6, 0.5)], mark.Ink[1]);
        Assert.Equal("Anna Meier", mark.Text);
    }

    [Fact]
    public void The_bake_draws_the_marks_into_the_pages_and_keeps_the_name()
    {
        var flattened = AnnotationBaking.Flatten(Page(), [Row(8, text: "Anna Meier", points: "0.2,0.4 0.3,0.5;0.4,0.6 0.5,0.4")]);

        using var pdf = Reader.PdfDocument.Open(flattened, new Reader.ParsingOptions());
        Assert.Empty(pdf.GetPage(1).GetAnnotations());
        Assert.Contains("Anna Meier", pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    private static AnnotationsClient.AnnotationInfo Row(int kind, string text = "", string? points = null) =>
        new(Guid.NewGuid(), 0, kind, 0.2, 0.4, kind is 7 or 8 ? null : 0.3, kind is 7 or 8 ? null : 0.2,
            text, "#000000", "Demo Admin", "etag", CanEdit: true, CanDelete: true, Points: points);

    private static byte[] Page()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }
}
