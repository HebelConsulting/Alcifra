using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Alcifra.ManualCapture;

// The annotation figures (#2455), their own partial because WebCapture.cs sits near the 1000-line ceiling and
// this is one responsibility: photographing the annotation layer — the tools over a marked-up page, a drawing in
// progress, the signature name prompt — plus the page reload this client's renderer needs afterwards (#2465).
public static partial class WebCapture
{
    // The seeded invoice: Demo Repository → Contracts → Alpenglas AG → "Invoice AG-2026-031". The only seeded
    // document carrying annotations (a highlight, a sticky note and a stamp), which is what makes any figure of
    // the annotation layer show marks rather than an empty page.
    private static async Task SelectInvoiceAsync(IPage page)
    {
        await page.Locator($".wb-tab[aria-label='{L("TabRepositories")}']").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var contracts = page.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" });
        await contracts.First.WaitForAsync(new() { Timeout = 15000 });
        await contracts.First.DblClickAsync();
        var alpenglas = page.Locator(".wb-list-row").Filter(new() { HasText = "Alpenglas AG" });
        await alpenglas.First.WaitForAsync(new() { Timeout = 15000 });
        await alpenglas.First.DblClickAsync();
        var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "Invoice AG-2026-031" });
        await doc.First.WaitForAsync(new() { Timeout = 15000 });
        await doc.First.ClickAsync();
        // Not `.wb-sysfields` alone: the FOLDER's detail is already on screen and satisfies that instantly, so
        // one run shot the folder's pane and the next the invoice's (#832's last straggler). The wait must name
        // the subject the figure is about.
        await page.Locator(".wb-sysfields").GetByText("Invoice AG-2026-031").First.WaitForAsync(new() { Timeout = 15000 });
        // The seeded sticky note's marker renders with the page: required, not best-effort — the annotation
        // figures exist to show marks, so a page that paints without them is a figure of the wrong thing.
        await page.Locator(".wb-pv-note").First.WaitForAsync(new() { Timeout = 15000 });
        await page.WaitForTimeoutAsync(1200);
    }

    // The annotation figures (#2455): the tools over a marked-up page, a drawing in progress, and the name a
    // signature is asked for at Finished. Every drawing is CANCELLED after its shot, so no later figure sees a
    // mark these three made, and the ink is blue — the seeded highlight is yellow, and the drawing must read as
    // the new thing on the page.
    private static async Task CaptureAnnotationsAsync(IPage page, string outDir)
    {
        try
        {
            await SelectInvoiceAsync(page);
            await FitPageAsync(page);
            var pane = page.Locator("[data-pane='preview']").First;
            await pane.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(outDir, "web-annotations.png") });
            Log.LogInformation("[web] annotations → web-annotations.png");
        }
        catch (Exception ex)
        {
            Skipped.Add("annotations");
            Log.LogWarning("[web] annotations skipped: {Reason}", ex.Message.Split('\n')[0]);
        }

        try
        {
            await SelectInvoiceAsync(page);
            await FitPageAsync(page);
            await BeginDrawingAsync(page, ".wb-pv-tool-pen");
            await DrawStrokesAsync(page);
            var pane = page.Locator("[data-pane='preview']").First;
            await pane.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(outDir, "web-annotations-session.png") });
            Log.LogInformation("[web] annotations-session → web-annotations-session.png");
        }
        catch (Exception ex)
        {
            Skipped.Add("annotations-session");
            Log.LogWarning("[web] annotations-session skipped: {Reason}", ex.Message.Split('\n')[0]);
        }
        finally
        {
            await TryCancelSessionAsync(page);
        }

        try
        {
            await SelectInvoiceAsync(page);
            await FitPageAsync(page);
            await BeginDrawingAsync(page, ".wb-pv-tool-sign");
            await DrawStrokesAsync(page, one: true);
            // Finishing is only enabled once the stroke is in the session, so a click that hangs here means the
            // stroke never arrived — fail fast and say so, rather than waiting out the 60 s page default.
            await page.Locator(".wb-pv-session-finish").First.ClickAsync(new() { Timeout = 15000 });
            var dialog = page.Locator(".mud-dialog").First;
            await dialog.WaitForAsync(new() { Timeout = 15000 });
            // A plausible invented name, typed but never confirmed (the dialog is dismissed), so the figure shows
            // what Finished asks for without filing anything.
            await dialog.GetByRole(AriaRole.Textbox).First.FillAsync("Anna Meier", new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(400);
            await ShotAsync(page, outDir, "annotations-sign");
        }
        catch (Exception ex)
        {
            Skipped.Add("annotations-sign");
            Log.LogWarning("[web] annotations-sign skipped: {Reason}", string.Join(" | ", ex.Message.Split('\n').Take(6)));
        }
        finally
        {
            // Dismissed by its OWN Cancel button, never Escape: Escape does not close this dialog and leaves the
            // Blazor renderer broken behind it ("removeChild of null" — found here, guarded by the UI suite's
            // Cancelling_the_name_prompt_then_the_session_leaves_the_workbench_working).
            try
            {
                var cancel = page.Locator(".mud-dialog").First.GetByRole(AriaRole.Button, new() { Name = L("Cancel") });
                if (await cancel.CountAsync() > 0 && await cancel.IsVisibleAsync())
                {
                    await cancel.ClickAsync();
                    await page.WaitForTimeoutAsync(600);
                }
            }
            catch (Exception ex) { Log.LogWarning("[web] could not close the signature dialog: {Reason}", ex.Message.Split('\n')[0]); }
            await TryCancelSessionAsync(page);
        }

        // This client's renderer can wedge in the harness's accumulated page state (#2465: a Blazor
        // "removeChild of null" right after the name dialog's close, then an error bar that intercepts every
        // later click). Not reproducible in the UI suite's isolated flow, so a fresh page is the honest
        // recovery: the remaining figures photograph a working app, and the crash is still logged above and
        // named in the issue rather than hidden.
        await RecoverFromRendererCrashAsync(page);
    }

    private static async Task RecoverFromRendererCrashAsync(IPage page)
    {
        var errorBar = page.Locator("#blazor-error-ui");
        if (await errorBar.CountAsync() == 0 || !await errorBar.IsVisibleAsync())
        {
            return;
        }

        Log.LogWarning("[web] the client error bar is up after the annotation figures (renderer crash, #2465) — "
            + "reloading the page for the remaining captures");
        await page.GotoAsync(page.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(".wb-tab").First.WaitForAsync(new() { Timeout = 60000 });
        await page.WaitForTimeoutAsync(1000);
    }

    // Fits the whole page in the pane, so a figure of the annotation layer shows every seeded mark: at fit-width
    // the sticky note and the stamp sit below the fold, and the pane is shorter than the page.
    private static async Task FitPageAsync(IPage page)
    {
        await page.Locator(".wb-pv-zoom-fit-page").First.ClickAsync();
        await page.WaitForTimeoutAsync(800);
    }

    // Arms a drawing tool and picks the palette's blue: the session bar proves the tool took, the swatch click
    // colours the whole drawing from its first stroke.
    private static async Task BeginDrawingAsync(IPage page, string toolSelector)
    {
        await page.Locator(toolSelector).First.ClickAsync();
        await page.Locator(".wb-pv-session-cancel").First.WaitForAsync(new() { Timeout = 10000 });
        // The Strong palette's blue (AnnotationPalette.Strong[5]) — the set every drawing kind starts in (#2469).
        // The literal must track the palette: #4FC3F7 was the pre-split blue, and after the split this wait timed
        // out, silently skipping the annotations-session and annotations-sign figures on every manual run.
        var blue = page.Locator(".wb-pv-palette .wb-pv-swatch[style*='#1976D2']").First;
        await blue.WaitForAsync(new() { Timeout = 10000 });
        await blue.ClickAsync();
        await page.WaitForTimeoutAsync(300);
    }

    // Draws on the page at FIXED relative coordinates, well clear of the seeded sticky note and stamp, so the
    // figure rasterises identically every run (a pointer path is as deterministic as the clock is frozen).
    private static async Task DrawStrokesAsync(IPage page, bool one = false)
    {
        var canvas = page.Locator(".wb-pv-page canvas").First;
        await canvas.WaitForAsync(new() { Timeout = 15000 });
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the preview page has no box");
        (double X, double Y)[][] strokes = one
            ? [[(0.46, 0.60), (0.50, 0.54), (0.54, 0.60), (0.58, 0.53), (0.62, 0.59), (0.66, 0.55)]]
            : [
                [(0.46, 0.52), (0.52, 0.49), (0.58, 0.53), (0.64, 0.50)],
                [(0.47, 0.60), (0.54, 0.57), (0.61, 0.61), (0.68, 0.55), (0.73, 0.58)],
            ];
        foreach (var stroke in strokes)
        {
            await page.Mouse.MoveAsync(box.X + (float)(stroke[0].X * box.Width), box.Y + (float)(stroke[0].Y * box.Height));
            await page.Mouse.DownAsync();
            foreach (var point in stroke.Skip(1))
            {
                await page.Mouse.MoveAsync(
                    box.X + (float)(point.X * box.Width), box.Y + (float)(point.Y * box.Height), new() { Steps = 8 });
            }

            await page.Mouse.UpAsync();
            await page.WaitForTimeoutAsync(250);
        }
    }

    // Leaves no drawing behind: a cancelled session files nothing, so every later figure photographs the archive
    // as the seed made it.
    private static async Task TryCancelSessionAsync(IPage page)
    {
        try
        {
            var cancel = page.Locator(".wb-pv-session-cancel").First;
            if (await cancel.CountAsync() > 0 && await cancel.IsVisibleAsync())
            {
                await cancel.ClickAsync();
                await page.WaitForTimeoutAsync(300);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("[web] could not cancel the drawing session: {Reason}", ex.Message.Split('\n')[0]);
        }
    }

}
