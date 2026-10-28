using Microsoft.Playwright;
using SimplArchive.Theming;

namespace SimplArchive.UiEndToEndTests;

// The brand's typeface reaches the browser (ADR 0991): /api/theme.css declares it and the API serves its files
// from /fonts. A missing file does not fail anything visible: the browser falls back to the next font in the stack
// and every page still renders, just not in the brand. So the assertion is what the BROWSER loaded, not what the
// stylesheet says.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebBrandFontTests
{
    private readonly SelfHostedAppFixture _app;

    public WebBrandFontTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_web_client_and_the_sign_in_page_are_set_in_the_brands_font()
    {
        var family = Brand.Shipped.Fonts.Ui;
        var page = await Ui.LoginAsync(_app);
        await AssertLoadedAsync(page, family, "the web client");

        // The sign-in page is server-rendered and never runs the client, so it is checked on its own.
        var signIn = await page.Context.NewPageAsync();
        await signIn.GotoAsync($"{_app.BaseUrl}/Account/Login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await AssertLoadedAsync(signIn, family, "the sign-in page");
    }

    private static async Task AssertLoadedAsync(IPage page, string family, string where)
    {
        // Wait until the page has asked for and settled its fonts, then read which faces actually LOADED.
        var loaded = await page.EvaluateAsync<string[]>(
            """
            async () => {
              await document.fonts.ready;
              await document.fonts.load('16px "' + getComputedStyle(document.body).fontFamily.split(',')[0].replaceAll('"', '').trim() + '"');
              return [...document.fonts].filter(f => f.status === 'loaded').map(f => f.family.replaceAll('"', ''));
            }
            """);
        var body = await page.EvaluateAsync<string>("() => getComputedStyle(document.body).fontFamily");

        // The first family the page asks for, as the browser spells it (quoted or not).
        Assert.Equal(family, body.Split(',')[0].Trim().Trim('"', '\''));
        Assert.True(
            loaded.Contains(family),
            $"{where} names {family} but the browser loaded no face of it (loaded: {string.Join(", ", loaded)}). "
            + "Check that /fonts serves the brand's woff2 files.");
    }
}
