using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The external-link landing page is the ONLY surface in the system rendered for someone outside it (ADR 0546) — and
// it is anonymous, which is exactly the connection the runtime-brand rule forbids a tenant brand on (ADR 1103,
// #2408). Colours may appear there; a mark, an asset or a name never. This drives the real flow — share a document
// from the workbench, then open the link in a fresh browser with no cookie from the session — and pins both halves:
// the page paints from a served palette at all, and it carries no logo anywhere, installation or tenant.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebExternalLinkLandingTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task The_landing_page_paints_from_served_colours_and_carries_no_logo()
    {
        var page = await Ui.LoginAsync(app);

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var folder in new[] { "Contracts", "Seewelle Telecom" })
        {
            var row = page.Locator(".wb-list-row").Filter(new() { HasText = folder });
            await row.First.WaitForAsync(new() { Timeout = 15000 });
            await row.First.DblClickAsync();
        }

        var doc = page.Locator(".wb-list-row").Filter(new() { HasText = "service agreement" });
        await doc.First.WaitForAsync(new() { Timeout = 15000 });
        await doc.First.ClickAsync();

        var url = await DocumentActionsUi.CreateExternalLinkAsync(page);

        // A stranger's browser: a fresh context, so no auth cookie from this session can make the page non-anonymous.
        await using var stranger = await app.Browser.NewContextAsync();
        var landing = await stranger.NewPageAsync();
        var response = await landing.GotoAsync(url);

        Assert.NotNull(response);
        Assert.Equal(200, response!.Status);

        // It painted: the document's name is on the card, so a page that failed to render cannot pass on absences.
        await Expect(landing.Locator("main h1")).ToBeVisibleAsync(new() { Timeout = 15000 });
        Assert.False(string.IsNullOrWhiteSpace(await landing.Locator("main h1").InnerTextAsync()));

        // The colours are served and reach the page: the installation's sheet defines the tokens the card's styles
        // read, which is what "colours may appear" means when the tenant has no pack (the pack-bearing case is the
        // API suite's).
        var accent = await landing.EvaluateAsync<string>(
            "getComputedStyle(document.documentElement).getPropertyValue('--sa-accent').trim()");
        Assert.False(string.IsNullOrWhiteSpace(accent));

        var html = await landing.ContentAsync();
        Assert.Contains("/api/theme.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/brand", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/brand/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wordmark", html, StringComparison.OrdinalIgnoreCase);
    }
}
