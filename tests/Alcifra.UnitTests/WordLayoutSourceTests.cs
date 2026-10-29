using Alcifra.ApiClient;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// Where find-in-document's words come from, and in which order the two sources are tried (#1402).
//
// The precedence is the behaviour: the SERVER first, because its layout is cached and — for an image — is the
// only possible source, since it comes from OCR this client does not have. Local extraction answers the case
// where the server cannot, which is the strict tier: there the text-layout endpoint refuses outright (409
// PLAINTEXT_CONTENT_REFUSED, ADR 0829), so the words must be computed from the bytes the card decrypted.
//
// Shared by the desktop and the phone (SimplArchiveMobile #14). Asserted with no api client at all, which is exactly the "server cannot answer" shape and needs no fixture.
public class WordLayoutSourceTests
{
    [Fact]
    public async Task With_no_server_answer_the_words_come_from_the_PDF_bytes()
    {
        var pdf = OnePagePdf("Rechnungsnummer");

        var pages = await WordLayoutSource.LoadAsync(versions: null, textLayoutUrl: null, pdfBytes: pdf);

        var page = Assert.Single(pages);
        Assert.Equal("Rechnungsnummer", Assert.Single(page).Text);
    }

    [Fact]
    public async Task A_scanned_page_yields_nothing_rather_than_failing()
    {
        // No text layer, so no words — the honest limit. The caller leaves the find affordance off rather than
        // offering a search that silently matches nothing, and the PREVIEW that already rendered is untouched.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(595, 842);

        var pages = await WordLayoutSource.LoadAsync(versions: null, textLayoutUrl: null, pdfBytes: builder.Build());

        Assert.All(pages, page => Assert.Empty(page));
    }

    [Fact]
    public async Task With_neither_a_server_answer_nor_bytes_it_returns_nothing()
    {
        // An image preview on a strict tenant is this case: the server refuses, and there are no PDF bytes to
        // read because the page is a picture. Nothing to find, and nothing that throws.
        Assert.Empty(await WordLayoutSource.LoadAsync(versions: null, textLayoutUrl: null, pdfBytes: null));
    }

    [Fact]
    public async Task Bytes_that_are_not_a_PDF_are_not_a_reason_to_fail()
    {
        // The preview has already rendered by the time the words are asked for, so losing it to a parser
        // exception would trade a working preview for a missing overlay.
        Assert.Empty(await WordLayoutSource.LoadAsync(
            versions: null, textLayoutUrl: null, pdfBytes: [0x00, 0x01, 0x02, 0x03]));
    }

    // The per-word rule both clients find by: any term, contained, ignoring case.
    [Theory]
    [InlineData("Invoices", "invoice", true)]
    [InlineData("Glasfassade", "glass fassade", true)]
    [InlineData("Rechnung", "invoice", false)]
    [InlineData("Rechnung", "   ", false)]
    public void A_word_matches_when_it_contains_any_term_ignoring_case(string word, string query, bool expected) =>
        Assert.Equal(expected, WordFind.Matches(word, WordFind.Terms(query)));

    private static byte[] OnePagePdf(string text)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(100, 700), font);
        return builder.Build();
    }
}
