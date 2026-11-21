using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// Creating an external link from the web client, with the browser in a NON-UTC timezone.
//
// That last clause is the entire point. `MudDatePicker` hands back a `DateTime` with `Kind = Local`, and
// `new DateTimeOffset(local, TimeSpan.Zero)` throws `Argument_OffsetLocalMismatch` unless the machine's own
// offset happens to be zero. So creating a link was broken for every user outside UTC — and broken *silently*,
// since the exception is unhandled: no snackbar, no error, the dialog simply did nothing when you pressed the
// button. It shipped in v0.1.5 and was found by hand, in a browser, at UTC+2.
//
// Every existing test missed it because CI runs in UTC, which makes the faulty expression legal. A test that
// does not set a timezone cannot catch this class of bug at all, however thoroughly it exercises the feature —
// so this one pins the context to Europe/Zurich. Same family as the Npgsql non-UTC `DateTimeOffset` write.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebExternalLinkCreateTests
{
    private readonly SelfHostedAppFixture _app;

    public WebExternalLinkCreateTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Creating_a_link_works_when_the_browser_is_not_in_UTC()
    {
        // Any zone with a non-zero offset reproduces it; Zurich is where it was found.
        var page = await Ui.LoginAsync(_app, configureContext: o => o.TimezoneId = "Europe/Zurich");

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

        // The URL is revealed exactly once, in the create dialog. Asserting on it rather than on "no error
        // appeared" matters: the bug produced no error either, which is precisely how it went unnoticed.
        var url = await DocumentActionsUi.CreateExternalLinkAsync(page);

        Assert.StartsWith("http", url, StringComparison.Ordinal);
        Assert.Contains("/api/external-links/", url, StringComparison.Ordinal);
    }
}
