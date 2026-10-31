using System.ComponentModel;
using System.Text;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Avalonia.Controls.Selection;

namespace Alcifra.UiEndToEndTests;

// The contents list keeps the selected document through a column sort and through a same-folder reload (2026-10-31).
//
// WHAT WAS WRONG. Both refilled the list: a sort cleared `Items` and added them back in the new order, a reload
// (a hold placed, an upload, Refresh) cleared it and added fresh rows. The ListBox lets go of a row that leaves
// its list, the binding wrote null into `SelectedItem`, and an empty selection shows the open FOLDER's detail. So
// a column-header click or a Refresh swapped the document the user was reading for the folder around it.
//
// WHY A SELECTION MODEL. Without a view nothing deselects, and a test of the view model alone passed before the
// fix. `Listing` stands in for the ListBox: Avalonia's own SelectionModel over `VisibleItems`, bound both ways
// as the XAML binds `SelectedItem`, so a row it loses reaches the view model exactly as it does on screen.
[Collection(UiCollection.Name)]
public class DesktopListKeepsSelectionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopListKeepsSelectionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_column_sort_keeps_the_selected_document_and_its_detail()
    {
        var (vm, folder, names) = await OpenFolderWithDocumentsAsync("sort");
        using var listing = new Listing(vm);
        var chosen = vm.Items.Single(i => i.Name == names[1]);
        vm.SelectedItem = chosen;
        await WaitForAsync(() => vm.DetailTitle == chosen.Name, seconds: 20);
        var shown = WatchDetailTitles(vm);

        vm.SortContentsCommand.Execute("name");   // ascending
        vm.SortContentsCommand.Execute("name");   // descending: the chosen row's neighbours change sides
        await Task.Delay(1200);

        Assert.Same(chosen, vm.SelectedItem);
        Assert.Same(chosen, listing.Selection.SelectedItem);
        Assert.Equal(chosen.Name, vm.DetailTitle);
        Assert.DoesNotContain(folder.Name, shown);
    }

    [Fact]
    public async Task A_same_folder_reload_keeps_the_selected_document_and_its_detail()
    {
        var (vm, folder, names) = await OpenFolderWithDocumentsAsync("reload");
        using var listing = new Listing(vm);
        var chosen = vm.Items.Single(i => i.Name == names[1]);
        vm.SelectedItem = chosen;
        await WaitForAsync(() => vm.DetailTitle == chosen.Name, seconds: 20);
        var shown = WatchDetailTitles(vm);

        await vm.RefreshCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.SelectedItem is { } s && s.Id == chosen.Id && vm.Items.Contains(s), seconds: 20);
        await Task.Delay(1200);

        // The FRESH row is selected (the old one left the list), so the pane reads the document's new state.
        var selected = Assert.IsType<NodeViewModel>(vm.SelectedItem);
        Assert.Equal(chosen.Id, selected.Id);
        Assert.Contains(selected, vm.VisibleItems);
        Assert.Same(selected, listing.Selection.SelectedItem);
        Assert.Equal(chosen.Name, vm.DetailTitle);
        Assert.DoesNotContain(folder.Name, shown);
    }

    // A folder of its own holding three documents, opened in a signed-in workbench.
    private async Task<(MainWindowViewModel Vm, TreeNodeViewModel Folder, string[] Names)> OpenFolderWithDocumentsAsync(string what)
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var tag = Guid.NewGuid().ToString("N")[..8];
        await api.Documents.CreateFolderAsync(repo.Href("children"), $"keep-{what}-{tag}");
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == $"keep-{what}-{tag}");
        string[] names = [$"a-{tag}", $"b-{tag}", $"c-{tag}"];
        foreach (var name in names)
        {
            await api.Documents.UploadFileAsync(folder.Href("children"), $"{name}.txt", Encoding.UTF8.GetBytes(name));
        }

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);
        var repositoryNode = vm.Tree.First(n => n.Id == repo.Id);
        await repositoryNode.EnsureExpandedAsync();
        var folderNode = repositoryNode.Children.Single(n => n.Id == folder.Id);
        vm.SelectedTreeNode = folderNode;
        await WaitForAsync(() => names.All(n => vm.VisibleItems.Any(i => i.Name == n)), seconds: 20);
        await WaitForAsync(() => vm.DetailTitle == folderNode.Name, seconds: 20);
        return (vm, folderNode, names);
    }

    // Every title the detail pane shows from here on, transient ones included.
    private static List<string> WatchDetailTitles(MainWindowViewModel vm)
    {
        var shown = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DetailTitle) && !string.IsNullOrEmpty(vm.DetailTitle))
            {
                shown.Add(vm.DetailTitle);
            }
        };
        return shown;
    }

    // The contents ListBox's selection: VisibleItems as its source, SelectedItem bound both ways.
    private sealed class Listing : IDisposable
    {
        private readonly MainWindowViewModel _vm;

        public SelectionModel<NodeViewModel> Selection { get; } = new() { SingleSelect = true };

        public Listing(MainWindowViewModel vm)
        {
            _vm = vm;
            Selection.Source = vm.VisibleItems;
            Selection.SelectionChanged += OnSelectionChanged;
            vm.PropertyChanged += OnViewModelChanged;
        }

        private void OnSelectionChanged(object? sender, SelectionModelSelectionChangedEventArgs<NodeViewModel> e)
        {
            if (!ReferenceEquals(_vm.SelectedItem, Selection.SelectedItem))
            {
                _vm.SelectedItem = Selection.SelectedItem;
            }
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainWindowViewModel.SelectedItem) || ReferenceEquals(_vm.SelectedItem, Selection.SelectedItem))
            {
                return;
            }

            if (_vm.SelectedItem is { } item && _vm.VisibleItems.Contains(item))
            {
                Selection.SelectedItem = item;
            }
            else
            {
                Selection.Clear();
            }
        }

        public void Dispose()
        {
            Selection.SelectionChanged -= OnSelectionChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
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
