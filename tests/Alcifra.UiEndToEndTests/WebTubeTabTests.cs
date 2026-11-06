using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The web's Tube tab (#2148): offered by the root's rel, channels with a lock, drafts marked, a filter. Its own home in the
// module collection, since only that app has a module advertising the rel; it holds the bar-pinning check WebTabBarTests
// makes for the tabs every tenant has.
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebTubeTabTests(ModuleAppFixture app)
{
    [Fact]
    public async Task The_tab_lists_the_channels_marks_the_lock_and_the_draft_and_filters()
    {
        var (http, _, _) = await app.ActivateTestModuleAsync();
        using var client = http;
        var page = await Ui.LoginAsync(app);

        await page.Locator(".wb-tab[aria-label='Tube']").First.ClickAsync();
        await Expect(page.Locator(".wb-tab-active")).ToHaveAttributeAsync("aria-label", "Tube");

        var tabs = await page.Locator(".wb-tabs").BoundingBoxAsync();
        var wb = await page.Locator(".wb").BoundingBoxAsync();
        Assert.InRange(wb!.Y + wb.Height - (tabs!.Y + tabs.Height), -1, 1);

        await Expect(page.GetByTestId("tube-channel")).ToHaveCountAsync(2);
        await Expect(page.Locator("[data-channel='Locked channel'] [data-testid='tube-lock']")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-channel='Open channel'] [data-testid='tube-lock']")).ToHaveCountAsync(0);

        await Expect(page.GetByTestId("tube-row")).ToHaveCountAsync(3);
        await Expect(page.GetByTestId("tube-draft")).ToHaveCountAsync(1);

        // Not the checkboxes, and not the hidden file input behind the channels' add (#2148).
        var filter = page.Locator(".wb-tube input:not([type=checkbox]):not([type=file])").First;
        await filter.FillAsync("keynote");
        await filter.BlurAsync();
        await Expect(page.GetByTestId("tube-row")).ToHaveCountAsync(1);
        await Expect(page.GetByTestId("tube-row")).ToContainTextAsync("Keynote");
    }
}
