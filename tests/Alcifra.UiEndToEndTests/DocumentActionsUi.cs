using Microsoft.Playwright;

namespace Alcifra.UiEndToEndTests;

/// <summary>
/// Where the document's own actions live since they left the details pane (ADR 0970): the ribbon's Documents group
/// and a list row's ⋮ menu. One place for the locators, so a test says WHICH surface it drives and nothing else.
/// </summary>
internal static class DocumentActionsUi
{
    /// <summary>The details pane — which must no longer carry any of these actions.</summary>
    public static ILocator DetailPane(IPage page) => page.Locator("[data-pane='index']");

    /// <summary>A ribbon button of the Documents group: follow, share, workflow-steps or actions.</summary>
    public static ILocator Ribbon(IPage page, string action) => page.Locator($".wb-ribbon [data-doc-action='{action}']");

    /// <summary>The items of whatever menu is open right now.</summary>
    public static ILocator OpenMenuItems(IPage page) => page.Locator(".mud-popover-open .mud-menu-item");

    /// <summary>Opens the ⋮ menu of the list row showing <paramref name="rowText"/>; returns the open menu's items.</summary>
    public static async Task<ILocator> OpenRowMenuAsync(IPage page, string rowText)
    {
        var row = page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = rowText });
        await row.Locator("button").Last.ClickAsync();
        var items = OpenMenuItems(page);
        await Assertions.Expect(items.First).ToBeVisibleAsync();
        return items;
    }

    /// <summary>Opens the ribbon's Actions menu (Bookings…, generic and module actions); returns its items.</summary>
    public static Task<ILocator> OpenRibbonActionsAsync(IPage page) => OpenRibbonMenuAsync(page, "actions");

    /// <summary>Opens one of the ribbon's menu buttons (`actions`, `workflow-steps`); returns its items.</summary>
    public static async Task<ILocator> OpenRibbonMenuAsync(IPage page, string action)
    {
        await Ribbon(page, action).ClickAsync();
        var items = OpenMenuItems(page);
        await Assertions.Expect(items.First).ToBeVisibleAsync();
        return items;
    }

    /// <summary>
    /// Opens the "Actions" submenu of an open ⋮ menu — hover then click, the <see cref="Ui.OpenNewSubmenuAsync"/>
    /// reasoning: a headless hover does not always settle into MudBlazor's popover.
    /// </summary>
    public static Task<ILocator> OpenActionsSubmenuAsync(IPage page, string expected) =>
        OpenSubmenuAsync(page, "Actions", expected);

    /// <summary>Opens the named submenu of an open ⋮ menu ("Actions", "Workflow step") and waits for an entry.</summary>
    public static async Task<ILocator> OpenSubmenuAsync(IPage page, string submenu, string expected)
    {
        var items = page.Locator(".mud-menu-item");
        var actions = items.Filter(new() { HasText = submenu }).First;
        await Assertions.Expect(actions).ToBeVisibleAsync();
        await actions.HoverAsync();

        var entry = items.Filter(new() { HasText = expected }).First;
        try
        {
            await Assertions.Expect(entry).ToBeVisibleAsync(new() { Timeout = 2000 });
        }
        catch (PlaywrightException)
        {
            await actions.ClickAsync();
            await Assertions.Expect(entry).ToBeVisibleAsync();
        }

        return entry;
    }
}
