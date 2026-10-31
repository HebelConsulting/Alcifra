using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A UI flow (ADR 0266): a search matching document content shows a highlighted snippet (the matched term wrapped
// in <em>). The seeded invoice's text (the supplier's letterhead, "…6010 Kriens…") is content-extracted (Tika) and
// indexed (OpenSearch), so searching a content-only word highlights it. Indexing is async, so the search polls.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebSearchHighlightTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSearchHighlightTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Content_match_shows_a_highlighted_snippet()
    {
        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Search\"]").First.ClickAsync();

        var input = page.Locator("input[placeholder*='Search by name']");
        // "Kriens" is only in the document's content (never in a name), so a hit + highlight prove content
        // extraction + indexing.
        var snippet = page.Locator(".wb-search-results .wb-list-row", new() { HasText = "Invoice AG-2026-031" }).Locator(".wb-search-snippet");

        for (var i = 0; i < 30; i++)
        {
            await input.FillAsync("Kriens");
            await input.PressAsync("Enter");
            if (await snippet.CountAsync() > 0 && await snippet.First.IsVisibleAsync())
            {
                break;
            }

            await Task.Delay(1000);
        }

        // The matched term is highlighted in the snippet.
        await Expect(snippet.Locator("em")).ToContainTextAsync("Kriens");
    }
}
