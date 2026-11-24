using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// #2453/#2460 (ADR 1107), the web's half: the pen collects a drawing SESSION — strokes until Finished files ONE
// multi-stroke annotation, with Undo/Redo stepping over them — and a signature (named at Finished) closes its
// session through the completion gate, which bakes IN THE BROWSER with the shared writer (the WASM proof).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebFreehandSessionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebFreehandSessionTests(SelfHostedAppFixture app) => _app = app;

    private async Task<IPage> OpenPdfAsync(string name)
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        // A REAL PDF (not a converted preview): the completion gate bakes the stored PDF, as on the phone.
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = name + ".pdf",
            MimeType = "application/pdf",
            Buffer = MinimalPdf(),
        });
        await list.GetByText(name).First.ClickAsync();
        await Expect(page.Locator(".wb-pv-tool-pen")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        return page;
    }

    // A one-page PDF, generated here: the gate only applies to a version whose OWN content is a PDF.
    private static byte[] MinimalPdf()
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static async Task StrokeAsync(IPage page, float x, float y, float dx, float dy)
    {
        var box = await page.Locator(".wb-pv-overlay").First.BoundingBoxAsync();
        Assert.NotNull(box);
        var cx = box!.X + x;
        var cy = box.Y + y;
        await page.Mouse.MoveAsync(cx, cy);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(cx + dx, cy + dy, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
    }

    [Fact]
    public async Task The_pen_files_one_multi_stroke_annotation_with_undo_and_redo()
    {
        var page = await OpenPdfAsync("draw-" + Guid.NewGuid().ToString("N")[..8]);

        await page.Locator(".wb-pv-tool-pen").ClickAsync();
        await Expect(page.Locator(".wb-pv-session-finish")).ToBeVisibleAsync();

        await StrokeAsync(page, 60, 60, 120, 10);
        await StrokeAsync(page, 70, 110, 110, 20);
        await Expect(page.Locator(".wb-pv-session polyline")).ToHaveCountAsync(2);

        // Undo steps back over the last stroke; Redo brings it back (#2453's grammar).
        await page.Locator(".wb-pv-session-undo").ClickAsync();
        await Expect(page.Locator(".wb-pv-session polyline")).ToHaveCountAsync(1);
        await page.Locator(".wb-pv-session-redo").ClickAsync();
        await Expect(page.Locator(".wb-pv-session polyline")).ToHaveCountAsync(2);

        // Finished files ONE annotation whose wire points carry both strokes (';' between them).
        var response = await page.RunAndWaitForResponseAsync(
            async () => await page.Locator(".wb-pv-session-finish").ClickAsync(),
            r => r.Request.Method == "POST" && r.Url.Contains("/annotations") && r.Status == 201);
        Assert.Contains(";", response.Request.PostData);

        // One mark, drawn as one SVG with a polyline per stroke.
        await Expect(page.Locator("svg.wb-pv-freehand")).ToHaveCountAsync(1);
        await Expect(page.Locator("svg.wb-pv-freehand polyline")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task A_signature_closes_its_session_through_the_gate()
    {
        var page = await OpenPdfAsync("sign-" + Guid.NewGuid().ToString("N")[..8]);

        await page.Locator(".wb-pv-tool-sign").ClickAsync();

        // Probe A: a drag in progress paints the LIVE stroke (the JS branch runs).
        var probeBox = await page.Locator(".wb-pv-overlay").First.BoundingBoxAsync();
        Assert.NotNull(probeBox);
        await page.Mouse.MoveAsync(probeBox!.X + 80, probeBox.Y + 140);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(probeBox.X + 170, probeBox.Y + 165, new MouseMoveOptions { Steps = 8 });
        await Expect(page.Locator(".wb-pv-session-live")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 5000 });
        await page.Mouse.UpAsync();

        // Probe B: after release the stroke belongs to the session (painted from the C# side).
        await Expect(page.Locator(".wb-pv-session polyline")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15000 });

        await StrokeAsync(page, 180, 150, 60, 15);

        // Both strokes reached the session (the layer is painted from the C# side, so this proves the round trip).
        await Expect(page.Locator(".wb-pv-session polyline")).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 15000 });

        // Finished asks for the name (the annotation dialog as the text prompt).
        await page.Locator(".wb-pv-session-finish").ClickAsync();
        var nameDialog = page.Locator(".mud-dialog");
        await Expect(nameDialog).ToBeVisibleAsync();
        await nameDialog.Locator("textarea").First.FillAsync("Anna Meier");
        await nameDialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // canEditContent and a signature — the gate is offered.
        var gate = page.Locator(".wb-pv-complete-signatures");
        await Expect(gate).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // The gate BAKES in the browser (the shared writer under WASM) and files the next version: the POST can
        // only follow a successful flatten, so a WASM-incompatible writer fails here by timing out.
        await gate.ClickAsync();
        var confirm = page.Locator(".mud-dialog");
        await Expect(confirm).ToBeVisibleAsync();
        await page.RunAndWaitForResponseAsync(
            async () => await confirm.GetByRole(AriaRole.Button, new() { Name = "All signatures completed" }).ClickAsync(),
            r => r.Request.Method == "POST" && r.Url.Contains("/versions") && r.Status == 201);
        await page.WaitForResponseAsync(r => r.Request.Method == "PUT" && r.Url.Contains("/versions/") && r.Status == 200);
    }
}
