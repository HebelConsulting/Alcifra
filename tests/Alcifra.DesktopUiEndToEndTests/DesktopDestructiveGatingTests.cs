using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// The destructive affordances are gated on what the server said about the row (#858).
//
// This is the POSITIVE half, and it needs a real server to exist. The headless `--screenshot --menu` capture
// shows the negative half convincingly — the demo tree's nodes are synthetic, carry no capabilities, and the
// menu correctly renders without Rename / Move to / Sort order / Delete. But a gate that hides everything is
// indistinguishable from a gate that is stuck off, so proving the flags ARRIVE and turn the menu back on is the
// half that says the wiring works rather than merely that it blocks.
//
// It asserts at the view-model level rather than by driving a menu: the menu's IsVisible bindings read exactly
// these properties, and Avalonia's context menu needs a pointer that a headless run does not have.
[Collection(UiCollection.Name)]
public class DesktopDestructiveGatingTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDestructiveGatingTests(SelfHostedAppFixture app) => _app = app;

    private async Task<MainWindowViewModel> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return vm;
    }

    [Fact]
    public async Task A_full_rights_caller_gets_the_capabilities_on_its_tree_nodes()
    {
        var vm = await OpenAsync();

        // The seeded admin owns their personal space outright, so every capability should be present. If these
        // came back false the menu would be empty for the one caller who may do everything — which is how a
        // gate that is silently stuck off would look.
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();

        var folder = personal.Children.FirstOrDefault(c => !c.IsLauncher);
        Assert.NotNull(folder);

        Assert.True(folder!.CanEditIndexData, "the owner may rename and reorder their own folder — the Rename and Sort order gate.");
        Assert.True(folder.CanDelete, "the owner may delete their own folder — the Delete gate.");
        Assert.True(folder.CanMove, "the owner may move their own folder — the Move to gate.");
    }

    [Fact]
    public async Task A_launcher_and_a_folder_the_core_holds_offer_nothing_destructive()
    {
        // The Personal launchers are not documents, so they never carry the capabilities. The Administration folder
        // and Users beneath it ARE documents, and an administrator's rights would allow everything — but the core
        // never lets them be deleted, renamed or moved (ADR 1066), so the row says so rather than offering a Delete
        // that ends in a 409.
        var vm = await OpenAsync();

        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        var administration = vm.Tree.Single(n => n.Name == "Administration");
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");

        foreach (var node in personal.Children.Where(n => n.IsLauncher).Concat(new[] { administration, users }))
        {
            Assert.False(node.CanDelete, $"{node.Name} offers Delete");
            Assert.False(node.CanEditIndexData, $"{node.Name} offers Rename");
            Assert.False(node.CanMove, $"{node.Name} offers Move to");
        }
    }

    [Fact]
    public async Task The_list_rows_carry_the_capabilities_too()
    {
        // The row menu reads CanRenameSelected / CanDeleteSelected off the SELECTED row, so the flags have to
        // survive the other parse path (the children listing into NodeViewModel) as well as the tree's.
        var vm = await OpenAsync();

        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        var folder = personal.Children.First(c => !c.IsLauncher);

        vm.SelectedTreeNode = folder;
        await vm.OpenFolderAsync(folder.Href("children"));

        var row = vm.Items.FirstOrDefault(i => !i.IsArchiveBack && !i.IsArchiveEntry);
        if (row is null)
        {
            return; // an empty folder proves nothing either way; the tree assertions above carry this case
        }

        Assert.True(row.CanEditIndexData || row.CanDelete || row.CanMove,
            "a row in the caller's own space arrived with no capabilities at all — the listing or the parse dropped them.");
    }
}
