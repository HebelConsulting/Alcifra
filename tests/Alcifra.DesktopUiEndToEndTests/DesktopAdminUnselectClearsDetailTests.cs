using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// Unselecting a row under Administration → Users leaves the detail pane describing the FOLDER the person stands in,
// never the row just deselected.
//
// Reported from live testing, when the branch was synthetic: select a user in the list, then unselect (click empty
// space, or cmd-click), and the pane still showed that user's mask. ADR 0559's rule is that a pane with nothing to
// show shows nothing — never the previous subject's values, which is a claim about the wrong object and looks like a
// fact. Users is a real folder now (ADR 1066), so the fallback on unselect — "describe the folder you are standing
// in" (ADR 0703) — has a subject, and that subject is what the pane must show.
[Collection(UiCollection.Name)]
public class DesktopAdminUnselectClearsDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminUnselectClearsDetailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Unselecting_a_user_row_describes_the_Users_folder_not_the_row()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Stand in another REAL folder first, so `_currentFolderId` holds something — the state a user actually
        // arrives with, and the one that makes a stale fallback possible.
        var repository = vm.Tree.First(n => n.Id != Guid.Empty && n.Name != "Administration");
        vm.SelectedTreeNode = repository;
        await WaitForAsync(() => vm.DetailTitle == repository.Name, seconds: 25);

        var administration = vm.Tree.Single(n => n.Name == "Administration");
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");

        // Waited on the rows being SHORTCUTS: the pane still holds the previous folder's rows until the load lands.
        vm.SelectedTreeNode = users;
        await WaitForAsync(() => vm.Items.Count > 0 && vm.Items.All(i => i.IsReference), seconds: 20);

        var person = vm.Items.First();

        // Waited on MASKLINE, not DetailTitle: the title is set at the top of the detail load and the mask
        // arrives from a later fetch, so waiting for the title and asserting the mask is a race — the same one
        // that made the sibling folder-detail test pass alone and fail beside a neighbour.
        vm.SelectedItem = person;
        await WaitForAsync(() => vm.DetailTitle == person.Name && vm.MaskLine.Contains("User Folder", StringComparison.Ordinal), seconds: 20);

        // The unselect: the pane falls back to the open folder (ADR 0703), which is Users — and nothing of the
        // person's remains on it.
        vm.SelectedItem = null;
        await WaitForAsync(() => vm.DetailTitle == users.Name && !vm.MaskLine.Contains("User Folder", StringComparison.Ordinal), seconds: 20);

        Assert.Equal(users.Name, vm.DetailTitle);
        Assert.DoesNotContain("User Folder", vm.MaskLine, StringComparison.Ordinal);
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
