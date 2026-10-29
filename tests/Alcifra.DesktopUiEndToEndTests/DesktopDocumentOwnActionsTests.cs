using Alcifra.ApiClient;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// The details pane holds only the details' own actions (ADR 0970): Edit/Save/Cancel, the tags, its collapse
// toggles, the workflow status line. Follow, Share by link, the workflow steps and every server-supplied action
// (Bookings…, the labeled generic actions, a module's pick-then-act actions) left it for the list's context menu
// AND the ribbon's Documents group. Make searchable lives ONLY in the Check-out tab's ribbon (owner decision).
//
// Two halves, each holding what the other cannot. The WIRING is a scan of the views — which control binds which
// command — because Avalonia realises a context menu only when it is opened, so a walk of a resting tree would
// report the menu's entries missing (DesktopGuidedTourTests' reasoning). The STATE the two surfaces bind to is
// asserted on the view-model: one list for the Actions submenu and the ribbon's Actions menu, rebuilt from its
// three sources and emptied the moment the subject changes (ADR 0559). The rendered result was looked at with the
// `--doc-menu`, `--ribbon-actions` and `--checkout` screenshot hooks.
public class DesktopDocumentOwnActionsTests
{
    // Each moved action, as the views bind it. Bookings, generic and module actions reach both surfaces through
    // the one combined list, so the list is what both must bind.
    private static readonly string[] MovedBindings =
    [
        "ToggleSubscriptionCommand",
        "ShowDocumentExternalLinksCommand",
        "DetailWorkflowEntries",
        "DetailActionEntries",
    ];

    // What the pane bound before ADR 0970, none of which may come back.
    private static readonly string[] LeftThePane =
    [
        "ToggleSubscriptionCommand",
        "ShowDocumentExternalLinksCommand",
        "MakeSearchableCommand",
        "OpenBookingsCommand",
        "ExecuteGenericActionCommand",
        "RunModuleActionCommand",
        "DetailActionEntries",
        "WorkflowTransitions",
        "OnWorkflowTransition",
        "DetailWorkflowEntries",
    ];

    [Fact]
    public void The_details_pane_binds_none_of_the_document_actions()
    {
        var pane = View("DocumentDetailPane.axaml");
        Assert.Contains("BeginEditCommand", pane, StringComparison.Ordinal); // the scan reads the right file
        Assert.All(LeftThePane, binding => Assert.DoesNotContain(binding, pane, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ContentsListPane.axaml")]
    [InlineData("RepositoriesRibbon.axaml")]
    public void The_context_menu_and_the_ribbon_both_offer_every_moved_action(string view)
    {
        var markup = View(view);
        Assert.All(MovedBindings, binding => Assert.Contains(binding, markup, StringComparison.Ordinal));
    }

    [Fact]
    public void Make_searchable_lives_only_in_the_check_out_ribbon()
    {
        Assert.Contains("MakeSearchableCommand", View("CheckoutRibbon.axaml"), StringComparison.Ordinal);
        foreach (var elsewhere in new[] { "CheckoutTab.axaml", "DocumentDetailPane.axaml", "ContentsListPane.axaml", "RepositoriesRibbon.axaml" })
        {
            Assert.DoesNotContain("MakeSearchable", View(elsewhere), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_pane_keeps_the_workflow_status_line()
    {
        Assert.Contains("WorkflowStateDisplay", View("DocumentDetailPane.axaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void One_actions_list_carries_bookings_generic_and_module_actions_and_empties_with_the_subject()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.HasDetailActionEntries);
        Assert.False(vm.HasDocumentOwnActions);

        vm.SetDetailGenericActions([new DocumentsClient.GenericActionInfo("accept-aircraft", "Accept aircraft", "POST", "api/x/accept")]);
        vm.SetDetailModuleActions([new DocumentsClient.ModuleActionInfo("fs:hand-over", "Hand over to…", "/options", "/commit", "email", "Who?")]);

        // The server's own labels, in the order the desktop builds them, each with a command to run.
        Assert.Equal(["Accept aircraft", "Hand over to…"], vm.DetailActionEntries.Select(e => e.Header).ToList());
        Assert.All(vm.DetailActionEntries, e => Assert.NotNull(e.Command));
        Assert.True(vm.HasDetailActionEntries);
        Assert.True(vm.HasDocumentOwnActions); // the context menu's separator shows with them

        // CLEARED when the subject changes (ADR 0559): an entry inherited from the previous document would run
        // against it from under the new one's name.
        vm.SetDetailGenericActions(null);
        vm.SetDetailModuleActions(null);
        Assert.Empty(vm.DetailActionEntries);
        Assert.False(vm.HasDetailActionEntries);
        Assert.False(vm.HasDocumentOwnActions);
    }

    [Fact]
    public void One_workflow_list_feeds_the_menu_and_the_ribbon_and_runs_through_the_view()
    {
        var vm = new MainWindowViewModel();
        MainWindowViewModel.WorkflowTransitionViewModel? ran = null;
        vm.RunWorkflowTransitionInView = t => { ran = t; return Task.CompletedTask; };

        vm.SetWorkflowTransitionsForScreenshot(["approve", "reject"]);
        Assert.Equal(2, vm.DetailWorkflowEntries.Count);
        Assert.True(vm.HasDetailWorkflowEntries);
        Assert.True(vm.HasDocumentOwnActions);

        // An entry runs the transition it was built from, through the view (reject opens the window there).
        vm.DetailWorkflowEntries[1].Command!.Execute(null);
        Assert.Equal("reject", ran?.Rel);

        // Cleared with the subject (ADR 0559).
        vm.SetWorkflowTransitionsForScreenshot([]);
        Assert.Empty(vm.DetailWorkflowEntries);
        Assert.False(vm.HasDetailWorkflowEntries);
    }

    [Fact]
    public void The_follow_toggle_names_what_it_will_do()
    {
        var vm = new MainWindowViewModel { DetailSubscribed = false };
        var follow = vm.SubscriptionMenuHeader;
        vm.DetailSubscribed = true;
        Assert.NotEqual(follow, vm.SubscriptionMenuHeader);
        Assert.Equal("mdi-bell", vm.SubscriptionIcon);
    }

    private static string View(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Alcifra.DesktopClient", "Views", file));
    }
}
