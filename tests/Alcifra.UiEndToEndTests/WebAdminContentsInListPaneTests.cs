using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// Selecting the Administration folder or Users beneath it LISTS their contents, so the column filter can find a
// user (#1160). Both are real folders now (ADR 1066): Administration holds Users, and Users holds one reference
// per personal space, which the list pane shows as rows like any folder's shortcuts.
//
// The assertion that matters is the FILTER one: rows merely arriving is not the feature. Typing a name and
// having the list narrow to it is.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebAdminContentsInListPaneTests
{
    private readonly SelfHostedAppFixture _app;

    public WebAdminContentsInListPaneTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_Administration_then_Users_lists_them_and_the_column_filter_finds_one()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // Target the content row so filtering by text hits the node's own label rather than an ancestor whose
        // expanded subtree also contains the text (the lesson WebAdminUsersTests records).
        var administration = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Administration" }).First;
        await Expect(administration).ToBeVisibleAsync();

        // CLICK THE LABEL, not the arrow: expanding is not selecting, and it is selection that fills the list.
        await administration.ClickAsync();

        // Administration holds exactly one entry. `.First`: the row shows "Users" twice — its name and its type,
        // since the folder wears the Users mask.
        await Expect(list.GetByText("Users", new() { Exact = true }).First).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Now Users itself — expand Administration to reach the node, then select it.
        await administration.Locator(".mud-treeview-item-arrow").ClickAsync();
        var users = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Users" }).First;
        await Expect(users).ToBeVisibleAsync();
        await users.ClickAsync();

        // One row per user. The demo admin's own space is the row whose name this test can state exactly.
        await Expect(list.GetByText("Demo Admin").First).ToBeVisibleAsync(new() { Timeout = 15000 });

        // THE POINT OF THE ISSUE. The column filter narrows the list — which is the whole reason for showing
        // these rows at all, and which nothing else in the suite would catch if the rows arrived unfilterable.
        var filter = list.Locator("input[placeholder]").First;
        await filter.FillAsync("Demo Admin");

        await Expect(list.GetByText("Demo Admin").First).ToBeVisibleAsync();

        // And a term that matches nobody empties it — so the filter is genuinely filtering rather than the
        // rows being present regardless of what is typed, which a one-sided assertion cannot tell apart.
        await filter.FillAsync("zzz-no-such-user-zzz");
        await Expect(list.GetByText("Demo Admin")).ToHaveCountAsync(0);
    }
}
