using Alcifra.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

// Acting on the MULTI-SELECTION (ADR "Bulk actions on selected documents"): what is selected, and moving,
// deleting, tagging or labelling all of it at once -- plus the same operations reached by DROPPING a set on a
// folder, which is the same act with a different gesture.
//
// Both paths funnel through one runner each (RunBulkAsync, RunDroppedBulkAsync) so that a partial failure is
// reported the same way however the user started it.
public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _hasBulkSelection;
    [ObservableProperty] private int _bulkSelectionCount;
    private List<NodeViewModel> _bulkSelection = [];

    // Called by the view's SelectionChanged: the current multi-selection (references / archive rows excluded).
    // The bulk-action bar shows when ≥2 real items are selected.
    public void SetBulkSelection(IEnumerable<NodeViewModel> selected)
    {
        _listSelection = [.. selected];
        _bulkSelection = _listSelection.Where(n => !n.IsReference && !n.IsArchiveEntry && !n.IsArchiveBack).ToList();
        BulkSelectionCount = _bulkSelection.Count;
        HasBulkSelection = _bulkSelection.Count >= 2;
        NotifyMoveSelectionChanged();
    }

    // The rows as selected, references and all: Move says why not where the bulk list would silently drop them (#2146).
    private List<NodeViewModel> _listSelection = [];

    private IReadOnlyList<NodeViewModel> MoveRows => _listSelection.Count > 0 ? _listSelection : SelectedItem is { } one ? [one] : [];

    private static bool NotMovableKind(NodeViewModel n) => n.IsReference || n.IsArchiveEntry || n.IsArchiveBack;

    public MoveOffer MoveOffer => MoveSelection.Of(MoveRows, NotMovableKind, n => n.CanMove);

    /// <summary>Move to… in the ribbon and the list's menu: one selected row moves it, several move them all (#2146).</summary>
    public bool CanMoveSelected => MoveOffer == MoveOffer.Allowed;

    public bool ShowMoveInMenu => MoveSelection.ShowsInMenu(MoveRows, NotMovableKind, n => n.CanMove);

    public string MoveHint => MoveSelection.WhyNotKey(MoveOffer) is { } key ? Strings.Get(key) : Strings.Get("CtxMoveTo");

    private void NotifyMoveSelectionChanged()
    {
        OnPropertyChanged(nameof(MoveOffer));
        OnPropertyChanged(nameof(CanMoveSelected));
        OnPropertyChanged(nameof(ShowMoveInMenu));
        OnPropertyChanged(nameof(MoveHint));
    }

    /// <summary>Moves what is selected, addressed from the rows themselves (ADR 0559); a folder among them changes the tree.</summary>
    public async Task MoveSelectedAsync(Guid targetFolderId)
    {
        var rows = MoveRows;
        if (MoveOffer != MoveOffer.Allowed)
        {
            return;
        }

        if (rows.Count == 1)
        {
            await MoveFolderAsync(rows[0].Href("self"), rows[0].Name, targetFolderId);
            return;
        }

        var foldersAmongThem = rows.Any(n => n.IsFolder);
        await BulkMoveAsync(targetFolderId);
        if (foldersAmongThem)
        {
            await ReloadTreeAsync();
        }
    }

    // A pure folder picker (no filing options) for choosing a bulk-move target.
    public FolderPickerViewModel CreateMoveTargetPickerViewModel(FolderPickerPurpose purpose = FolderPickerPurpose.Move) =>
        _api is null ? null! : new FolderPickerViewModel(_api, null, bulk: true, purpose);

    // The tenant tag catalog, for the bulk add-tags dialog's autocomplete.
    public async Task<IReadOnlyList<string>> GetTagCatalogAsync()
    {
        if (_api is null)
        {
            return [];
        }

        try { return await _api.Tags.GetTagCatalogAsync(); } catch (Exception) { return []; }
    }

    public Task BulkMoveAsync(Guid targetFolderId) =>
        RunBulkAsync(ids => _api!.Documents.BulkMoveAsync(ids, targetFolderId), "moved");

    public Task BulkDeleteAsync() =>
        RunBulkAsync(ids => _api!.Documents.BulkDeleteAsync(ids), "deleted");

    public Task BulkAddTagsAsync(IReadOnlyList<string> tags) =>
        RunBulkAsync(ids => _api!.Documents.BulkAddTagsAsync(ids, tags), "tagged");

    public Task BulkSetSensitivityAsync(Guid? labelId) =>
        RunBulkAsync(ids => _api!.Documents.BulkSetSensitivityAsync(ids, labelId), "classified");

    private async Task RunBulkAsync(Func<IReadOnlyList<Guid>, Task<BulkResult>> action, string verb)
    {
        if (_api is null || _currentFolderId is not { } folderId || _bulkSelection.Count == 0)
        {
            return;
        }

        var ids = _bulkSelection.Select(n => n.Id).ToList();
        try
        {
            var result = await action(ids);
            Status = string.Format(Strings.Get("StBulkResult"), result.Succeeded, verb) + (result.Skipped > 0 ? string.Format(Strings.Get("StBulkSkipped"), result.Skipped) : ".");
            SetBulkSelection([]);
            ClearDetail();
            await LoadFolderContentsAsync(folderId);
        }
        catch (Alcifra.ApiClient.ApiActionException e)
        {
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrBulk"), e.Message));
        }
    }

    // Files a reference (shortcut) to a dragged item into a folder. node.Id is the target (for a reference
    // source it's the underlying item, so referencing a reference just points at the same item).
    public async Task ReferenceNodeAsync(NodeViewModel node, string targetReferencesHref)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            return;
        }

        try
        {
            await _api.References.CreateReferenceAsync(targetReferencesHref, node.Id);
            Status = string.Format(Strings.Get("StPlacedRef"), node.Name);
            await LoadFolderContentsAsync(folderId);
        }
        catch (Alcifra.ApiClient.ApiActionException e)
        {
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrPlaceRef"), e.Message));
        }
    }

    // Move / reference a specific set of dragged item ids into a target folder — used by drag-drop, which operates
    // on the DRAGGED selection (which may differ from the persisted multi-selection that RunBulkAsync uses).
    public Task BulkMoveNodesAsync(IReadOnlyList<Guid> ids, Guid targetFolderId) =>
        RunDroppedBulkAsync(() => _api!.Documents.BulkMoveAsync(ids, targetFolderId), "moved", ids.Count);

    public Task BulkReferenceNodesAsync(IReadOnlyList<Guid> ids, Guid targetFolderId) =>
        RunDroppedBulkAsync(() => _api!.Documents.BulkReferenceAsync(ids, targetFolderId), "referenced", ids.Count);

    private async Task RunDroppedBulkAsync(Func<Task<BulkResult>> action, string verb, int count)
    {
        if (_api is null || _currentFolderId is not { } folderId || count == 0)
        {
            return;
        }

        try
        {
            var result = await action();
            Status = string.Format(Strings.Get("StBulkResult"), result.Succeeded, verb) + (result.Skipped > 0 ? string.Format(Strings.Get("StBulkSkipped"), result.Skipped) : ".");
            await LoadFolderContentsAsync(folderId);
        }
        catch (Alcifra.ApiClient.ApiActionException e)
        {
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrBulk"), e.Message));
        }
    }
}
