using MudBlazor;
using SimplArchive.Client.Components.Panes;
using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;
using SimplArchive.Client.Services;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// The document's own actions that are not its details (ADR 0970). The details pane holds only Edit/Save/Cancel,
// the tags, its collapse toggles and the workflow status line; following, sharing, the workflow steps and every
// server-supplied action moved to
// the ribbon's Documents group and a row's ⋮ menu, matching the desktop's ribbon and context menu (ADR 0511). Its
// own partial because the shell is at its size ceiling (WorkbenchShellSizeTests).
public partial class Home : IDocumentActionHost
{
    /// <summary>
    /// The offer for <paramref name="node"/>: the details pane's subject gets what its detail load found, every
    /// other row gets nothing — a ⋮ menu opened on an unselected row must not offer the selection's actions under
    /// that row's name (ADR 0559). Follow and Share also need the subject to BE the selected row, because they
    /// are addressed from it; the open folder described with nothing selected has its own follow in the tree.
    /// </summary>
    private DocumentActionOffer OfferFor(BrowseNode? node)
    {
        if (node is null || Detail.Node is not { } subject || subject.Id != node.Id)
        {
            return DocumentActionOffer.None;
        }

        var isSelection = _selectedItem?.Id == node.Id;
        return new DocumentActionOffer(
            CanFollow: isSelection && Detail.Links?.ContainsKey("subscription") == true,
            Subscribed: Detail.Subscribed,
            CanShare: isSelection && Detail.ExternalLinksHref is not null,
            WorkflowSteps: isSelection ? Detail.OfferedTransitions.ToList() : [],
            CanBook: Detail.Links?.ContainsKey("bookings") == true,
            GenericActions: Detail.GenericActions,
            ModuleActions: Detail.ModuleActions,
            Busy: Detail.Busy);
    }

    // Each call arrives through a menu item's or ribbon button's OWN EventCallback, so Blazor re-renders that small
    // component afterwards, never this shell — and the shell renders the ribbon, the menus and the pane's subject.
    // Without the explicit render a workflow step left the ribbon offering the OLD state's steps.
    Task IDocumentActionHost.ToggleFollowAsync() => ThenRenderAsync(ToggleSubscriptionAsync());

    Task IDocumentActionHost.ShareAsync() => ThenRenderAsync(OpenExternalLinksDialogAsync());

    Task IDocumentActionHost.RunWorkflowStepAsync(string rel) => ThenRenderAsync(DoWorkflowTransitionAsync(rel));

    Task IDocumentActionHost.OpenBookingsAsync() => ThenRenderAsync(OpenBookingsDialogAsync());

    Task IDocumentActionHost.RunGenericActionAsync(LinkResponse action) => ThenRenderAsync(ExecuteGenericActionAsync(action));

    Task IDocumentActionHost.RunModuleActionAsync(ModuleActionDto action) => ThenRenderAsync(RunModuleActionAsync(action));

    private async Task ThenRenderAsync(Task action)
    {
        await action;
        StateHasChanged();
    }

    // Compare two versions of a document as an inline text diff (ADR "Document version comparison"). Addressed from
    // the ROW the ⋮ menu belongs to (ADR 0559) — it moved there from the details pane with the other actions.
    private async Task OpenCompareVersionsAsync(BrowseNode item)
    {
        var parameters = new DialogParameters { ["DocumentId"] = item.Id, ["DocumentName"] = item.Name, ["VersionsHref"] = item.Links?.GetValueOrDefault("versions") };
        var dialog = await DialogService.ShowAsync<Dialogs.CompareVersionsDialog>(Strings.Get("CvTitle"), parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        // A restore (ADR "Version restore") closes the dialog with true — reselect the document so the detail
        // pane + preview reflect the new current version.
        if ((await dialog.Result) is { Canceled: false, Data: true })
        {
            await SelectItemAsync(item);
        }
    }
}
