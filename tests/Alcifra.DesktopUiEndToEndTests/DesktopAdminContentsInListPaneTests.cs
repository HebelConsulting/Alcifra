using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// Selecting the Administration folder or Users beneath it LISTS their contents, so the column filter can find a user
// (#1160) — and, since the two are real folders (ADR 1066), the detail pane describes them like any folder (#408).
//
// WHY THE ASSERTIONS ARE WHAT THEY ARE. A row is only useful here if the FILTER can match it, so the test asserts
// the row a person would type for — the admin's own space, whose name this test can state exactly — rather than
// merely that some rows arrived. It asserts the row carries the real repository id, because that is what makes
// opening one browse into the user's space exactly as the tree node does. And it holds the TREE and the LIST to
// the same rows: both read the Users folder's references, and two surfaces describing one folder must not disagree.
[Collection(UiCollection.Name)]
public class DesktopAdminContentsInListPaneTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminContentsInListPaneTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Administration_lists_Users_and_Users_lists_every_users_space_as_the_tree_does()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        var administration = vm.Tree.Single(n => n.Name == "Administration");

        // 1. Administration lists its single Users entry — a real child folder, from the children listing.
        vm.SelectedTreeNode = administration;
        await WaitForAsync(() => vm.Items.Count > 0);
        Assert.Equal("Users", Assert.Single(vm.Items).Name);

        // 2. Users lists one row per user — the references the core placed, one per personal space.
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");

        // Waited on the ROWS BEING SHORTCUTS, not on "any row": the pane still holds Administration's one row
        // (Users, a real child) until the load lands, and that row has an id too.
        vm.SelectedTreeNode = users;
        await WaitForAsync(() => vm.Items.Count > 0 && vm.Items.All(i => i.IsReference));

        var rows = vm.Items.ToList();
        Assert.NotEmpty(rows);

        // The admin themself is always present, and is the row whose name this test can state exactly: a personal
        // space is named after its owner (ADR 0671), which is also what the filter matches on.
        Assert.True(rows.Any(r => r.Name == SelfHostedAppFixture.AdminDisplayName),
            $"no row named {SelfHostedAppFixture.AdminDisplayName}; rows: {string.Join("; ", rows.Select(r => $"{r.Name} | {r.DocumentType} | {r.CreatedBy} | ref={r.IsReference}"))}");
        var admin = rows.Single(r => r.Name == SelfHostedAppFixture.AdminDisplayName);
        Assert.NotEqual(Guid.Empty, admin.Id);

        // 3. The TREE and the LIST describe the same thing — the same ids, the same names.
        await users.EnsureExpandedAsync();
        var treeIds = users.Children.Where(n => n.Id != Guid.Empty).Select(n => n.Id).OrderBy(id => id).ToList();
        var listIds = rows.Select(r => r.Id).OrderBy(id => id).ToList();
        Assert.Equal(treeIds, listIds);

        var treeNames = users.Children.Where(n => n.Id != Guid.Empty).Select(n => n.Name).OrderBy(n => n).ToList();
        var listNames = rows.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(treeNames, listNames);
    }

    [Fact]
    public async Task An_admin_folder_describes_itself_not_the_previously_selected_document()
    {
        // The Administration folder is a document with a mask, so selecting it in the tree describes IT (#408) — and
        // the rule this pins is ADR 0559's: the pane never keeps the previously-selected document's values, which
        // would be a claim about the wrong object and is invisible unless asserted.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Stand in another folder first and select something, so the detail pane HAS a subject to leak.
        var repository = vm.Tree.First(n => n.Id != Guid.Empty && n.Name != "Administration");
        vm.SelectedTreeNode = repository;
        await WaitForAsync(() => vm.Items.Count > 0, seconds: 20);

        vm.SelectedItem = vm.Items.FirstOrDefault();
        await Task.Delay(500);

        vm.SelectedTreeNode = vm.Tree.Single(n => n.Name == "Administration");
        await WaitForAsync(() => vm.Items.Count == 1 && vm.Items[0].Name == "Users");
        await WaitForAsync(() => vm.DetailTitle == "Administration", seconds: 25);

        Assert.Equal("Administration", vm.DetailTitle);
        Assert.True(vm.DetailIsFolder, "the pane should be describing a FOLDER");
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {seconds}s waiting for the condition.");
    }
}
