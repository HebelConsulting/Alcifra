using Microsoft.Playwright;

namespace Alcifra.UiEndToEndTests;

// The brand's wordmark (ADR 0992) in the app bar: exactly one of its two files shows, the one matching the theme,
// and it actually LOADED (a broken image keeps its alt text, which reads as the name and passes any text check).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebBrandWordmarkTests
{
    private readonly SelfHostedAppFixture _app;

    public WebBrandWordmarkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_app_bar_shows_the_wordmark_that_matches_the_theme()
    {
        var page = await Ui.LoginAsync(_app);
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });

        var light = page.Locator(".wb-appbar img.sa-wordmark-light");
        var dark = page.Locator(".wb-appbar img.sa-wordmark-dark");
        await Assertions.Expect(light).ToBeVisibleAsync();
        await Assertions.Expect(dark).ToBeHiddenAsync();
        Assert.True(await LoadedAsync(light), "the light wordmark did not load: check that /brand serves it.");

        // The in-app choice, which wins over the operating system's.
        await page.GetByTitle("Toggle light/dark mode").First.ClickAsync();
        await Assertions.Expect(dark).ToBeVisibleAsync();
        await Assertions.Expect(light).ToBeHiddenAsync();
        Assert.True(await LoadedAsync(dark), "the dark wordmark did not load: check that /brand serves it.");
    }

    [Fact]
    public async Task The_sign_in_page_shows_the_wordmark()
    {
        var page = await Ui.LoginAsync(_app);
        var signIn = await page.Context.NewPageAsync();
        await signIn.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await signIn.GotoAsync($"{_app.BaseUrl}/Account/Login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var light = signIn.Locator(".brand img.sa-wordmark-light").First;
        await Assertions.Expect(light).ToBeVisibleAsync();
        Assert.True(await LoadedAsync(light), "the sign-in page's wordmark did not load.");
    }

    private static Task<bool> LoadedAsync(ILocator image) =>
        image.EvaluateAsync<bool>("img => img.complete && img.naturalWidth > 0");
}
