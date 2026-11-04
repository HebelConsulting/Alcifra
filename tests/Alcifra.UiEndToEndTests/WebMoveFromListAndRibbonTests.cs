using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// Move to… from the ribbon and the list's row menu (#2146): one selected row moves it, several move them all with one picker,
// and a reference in the selection disables it and says why. Creates its own folders (ADR 0884).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebMoveFromListAndRibbonTests
{
    private readonly SelfHostedAppFixture _app;

    public WebMoveFromListAndRibbonTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_ribbon_and_the_row_menu_move_one_row_or_all_the_selected_ones()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (box, one, two, three) = ($"box-{suffix}", $"one-{suffix}", $"two-{suffix}", $"three-{suffix}");
        var list = page.Locator("[data-pane='list']");
        var ribbon = page.GetByTestId("ribbon-move");

        var nextFolderName = string.Empty;
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };
        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        await page.GetByText("Demo Repository").First.ClickAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(one);
        await NewFolderAsync(two);
        await NewFolderAsync(three);

        // One row, by the ribbon.
        await list.Locator(".wb-list-row").Filter(new() { HasText = one }).ClickAsync();
        await Expect(ribbon).ToBeEnabledAsync();
        await ribbon.ClickAsync();
        await PickRootAsync(page);
        await Expect(list.GetByText(one)).Not.ToBeVisibleAsync();

        // Both remaining rows, by the ribbon's single picker.
        var ctrl = new Dictionary<string, object> { ["ctrlKey"] = true, ["bubbles"] = true };
        await list.Locator(".wb-list-row").Filter(new() { HasText = two }).DispatchEventAsync("click", ctrl);
        await list.Locator(".wb-list-row").Filter(new() { HasText = three }).DispatchEventAsync("click", ctrl);
        await Expect(ribbon).ToBeEnabledAsync();
        await ribbon.ClickAsync();
        await PickRootAsync(page);
        await Expect(list.GetByText(two)).Not.ToBeVisibleAsync();
        await Expect(list.GetByText(three)).Not.ToBeVisibleAsync();

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var moved in new[] { one, two, three })
        {
            await Expect(list.GetByText(moved)).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task A_reference_has_no_Move_and_the_ribbon_says_why()
    {
        var page = await Ui.LoginAsync(_app);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (box, item) = ($"box-{suffix}", $"item-{suffix}");
        var list = page.Locator("[data-pane='list']");
        var ribbon = page.GetByTestId("ribbon-move");

        var nextFolderName = string.Empty;
        page.Dialog += (_, dialog) => { _ = dialog.AcceptAsync(nextFolderName); };
        async Task NewFolderAsync(string name)
        {
            nextFolderName = name;
            await page.Locator(".wb-ribbon [aria-label=\"New folder\"]").First.ClickAsync();
            await Expect(list.GetByText(name)).ToBeVisibleAsync();
        }

        await page.GetByText("Demo Repository").First.ClickAsync();
        await NewFolderAsync(box);
        await list.GetByText(box).First.DblClickAsync();
        await NewFolderAsync(item);

        await list.Locator(".wb-list-row").Filter(new() { HasText = item }).Locator("button").Last.ClickAsync();
        await page.GetByText("Place reference in").First.ClickAsync();
        await PickRootAsync(page);
        await page.GetByText("Demo Repository").First.ClickAsync();

        var shortcut = list.Locator(".wb-list-row").Filter(new() { HasText = item });
        await shortcut.ClickAsync();
        await Expect(ribbon).ToBeDisabledAsync();
        await Expect(ribbon).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("References cannot be moved"));

        await shortcut.Locator("button").Last.ClickAsync();
        await Expect(page.Locator(".mud-popover-open").GetByText("Move to")).Not.ToBeVisibleAsync();
    }

    private static async Task PickRootAsync(IPage page)
    {
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).ClickAsync();
    }
}
