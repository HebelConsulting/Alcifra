using Microsoft.Playwright;

namespace Alcifra.UiEndToEndTests;

// ADR 1106: the version's "PDF with annotations" ribbon action — the web client fetches the ephemeral export with
// its authenticated client and hands the bytes to the browser to save (an API route behind the bearer token, so a
// plain navigation cannot do it).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebAnnotatedPdfExportTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAnnotatedPdfExportTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_ribbon_saves_the_version_as_a_pdf_with_annotations()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        // The seeded PDF the other download tests use: Demo Repository → Contracts → Alpenglas AG → the invoice.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await list.GetByText("Contracts").First.DblClickAsync();
        await list.GetByText("Alpenglas AG").First.DblClickAsync();
        await list.GetByText("Invoice AG-2026-031").First.ClickAsync();

        // The rel arrives with the version the detail load reads: the button turns enabled (it is disabled, not
        // hidden, until then — ADR 0550).
        var export = page.GetByTestId("ribbon-annotated-pdf");
        await Assertions.Expect(export).ToBeEnabledAsync(new() { Timeout = 30000 });

        // The save goes through the shared JS helper; capture what it was asked to save instead of a real file.
        await page.EvaluateAsync("() => { window.__saved = null; window.downloadFileFromStream = async (n) => { window.__saved = n; }; }");
        await export.ClickAsync();
        await page.WaitForFunctionAsync("() => window.__saved !== null", null, new() { Timeout = 15000 });

        var name = await page.EvaluateAsync<string>("() => window.__saved");
        Assert.EndsWith("(annotations).pdf", name);
    }
}
