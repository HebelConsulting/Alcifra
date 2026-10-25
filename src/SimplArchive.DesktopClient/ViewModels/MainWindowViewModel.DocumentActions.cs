using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The selected document's own actions, offered OUTSIDE the details pane (ADR 0970): the pane holds only what
// belongs to the details — Edit/Save/Cancel, the tags, its collapse toggles, the workflow status line — and
// everything the user does TO the
// document lives where the other document actions already are, the list's context menu and the ribbon's
// Documents group. Both read the state below, which is the details pane's subject: the shell owns that selection
// (ADR 0661), a right-click selects the row before its menu opens, and every address here is cleared the moment
// the subject changes (ADR 0559), so an entry that has not caught up is ABSENT rather than the previous row's.
public partial class MainWindowViewModel
{
    /// <summary>
    /// The server-supplied actions on the subject — Bookings…, the labeled generic actions (ADR 0743) and the
    /// module's pick-then-act actions (ADR 0786) — as ONE list, because the context menu and the ribbon each show
    /// them as one "Actions" submenu. Avalonia's menu takes literal items or a bound collection, never both, so
    /// a submenu is forced in the context menu; the ribbon uses the same one so the two read alike.
    /// </summary>
    public ObservableCollection<TreeMenuEntry> DetailActionEntries { get; } = [];

    public bool HasDetailActionEntries => DetailActionEntries.Count > 0;

    /// <summary>Whether Follow/Unfollow can be offered: the subject advertised `subscription` AND its current
    /// state has been read — before that the toggle would claim a state it does not know.</summary>
    [ObservableProperty] private bool _canToggleSubscription;

    /// <summary>
    /// Whether "Change audience…" is on offer (ADR 0975): the server says this caller filed the current version of a
    /// sealed document it may still change. The server decides authorship — the ribbon must not try to guess it from a
    /// name — and it refuses a re-seal from anybody else even when a caller asks directly.
    /// </summary>
    [ObservableProperty] private bool _canChangeAudience;

    /// <summary>The context menu's short label; the ribbon's tooltip is <see cref="SubscriptionTip"/>.</summary>
    public string SubscriptionMenuHeader => Strings.Get(DetailSubscribed ? "CtxUnfollowDoc" : "CtxFollowDoc");

    public string SubscriptionIcon => DetailSubscribed ? "mdi-bell" : "mdi-bell-outline";

    public string SubscriptionTip => Strings.Get(DetailSubscribed ? "UnfollowDoc" : "FollowDoc");

    /// <summary>
    /// The workflow transitions the subject's current version advertises (#691), as menu entries — the context
    /// menu's "Workflow step" submenu and the ribbon's menu button, one list for both (ADR 0970). The pane keeps
    /// only the status LINE, which is a detail.
    /// </summary>
    public ObservableCollection<TreeMenuEntry> DetailWorkflowEntries { get; } = [];

    public bool HasDetailWorkflowEntries => DetailWorkflowEntries.Count > 0;

    /// <summary>
    /// Runs one transition: approve, submit and release act; reject and reassign open the workflow window that
    /// can ask for a reason or a reviewer. View-supplied (a window), so a settable callback (ADR 0730).
    /// </summary>
    public Func<WorkflowTransitionViewModel, Task>? RunWorkflowTransitionInView { get; set; }

    /// <summary>Whether any of the actions below is on offer — the context menu's separator above them.</summary>
    public bool HasDocumentOwnActions => CanToggleSubscription || CanShareDocument || CanChangeAudience || HasDetailActionEntries || HasDetailWorkflowEntries;

    /// <summary>Empties the offered transitions with their menu entries — the subject changed (ADR 0559).</summary>
    private void ClearWorkflowTransitions()
    {
        WorkflowTransitions.Clear();
        RebuildWorkflowEntries();
    }

    /// <summary>Rebuilds <see cref="DetailWorkflowEntries"/> from <see cref="WorkflowTransitions"/>. Each entry
    /// carries the transition it was built from, so it follows that transition's own href (ADR 0559).</summary>
    private void RebuildWorkflowEntries()
    {
        DetailWorkflowEntries.Clear();
        foreach (var transition in WorkflowTransitions)
        {
            DetailWorkflowEntries.Add(TreeMenuEntry.Create(transition.Label, TransitionIcon(transition.Rel),
                () => Safe.Fire(() => RunWorkflowTransitionInView?.Invoke(transition) ?? Task.CompletedTask)));
        }

        OnPropertyChanged(nameof(HasDetailWorkflowEntries));
        OnPropertyChanged(nameof(HasDocumentOwnActions));
    }

    /// <summary>The `--workflow-steps` screenshot hook: transitions as the server would offer a reviewer. The hrefs
    /// are obviously fake (nothing follows them), so no realistic api/ path is composed here (ADR 0543).</summary>
    internal void SetWorkflowTransitionsForScreenshot(IEnumerable<string> rels)
    {
        WorkflowTransitions.Clear();
        foreach (var rel in rels)
        {
            WorkflowTransitions.Add(new WorkflowTransitionViewModel(rel, TransitionLabel(rel), $"(demo)/{rel}"));
        }

        RebuildWorkflowEntries();
    }

    private static string TransitionIcon(string rel) => rel switch
    {
        "submit" => "mdi-send-outline",
        "approve" => "mdi-check",
        "reject" => "mdi-close",
        "reassign" => "mdi-account-switch-outline",
        "release" => "mdi-check-all",
        _ => "mdi-sitemap-outline",
    };

    partial void OnCanToggleSubscriptionChanged(bool value) => OnPropertyChanged(nameof(HasDocumentOwnActions));

    partial void OnCanShareDocumentChanged(bool value) => OnPropertyChanged(nameof(HasDocumentOwnActions));

    partial void OnCanChangeAudienceChanged(bool value) => OnPropertyChanged(nameof(HasDocumentOwnActions));

    partial void OnDetailSubscribedChanged(bool value)
    {
        OnPropertyChanged(nameof(SubscriptionIcon));
        OnPropertyChanged(nameof(SubscriptionTip));
        OnPropertyChanged(nameof(SubscriptionMenuHeader));
    }

    /// <summary>Rebuilds <see cref="DetailActionEntries"/> from the subject's rels and actions — called whenever
    /// any of the three sources changes, including when they are cleared for a new subject.</summary>
    private void RebuildDetailActionEntries()
    {
        DetailActionEntries.Clear();
        if (CanOpenBookings)
        {
            DetailActionEntries.Add(TreeMenuEntry.Create(Strings.Get("BookingsButton"), "mdi-calendar-clock",
                () => OpenBookingsCommand.Execute(null)));
        }

        // Each entry carries the action it was built from, so running it is addressed from the action's own
        // hrefs (ADR 0559), never from whatever the pane has loaded by the time the user clicks.
        foreach (var action in DetailGenericActions)
        {
            DetailActionEntries.Add(TreeMenuEntry.Create(action.Label, "mdi-lightning-bolt-outline",
                () => ExecuteGenericActionCommand.Execute(action)));
        }

        foreach (var action in DetailModuleActions)
        {
            DetailActionEntries.Add(TreeMenuEntry.Create(action.Label, "mdi-puzzle-outline",
                () => RunModuleActionCommand.Execute(action)));
        }

        OnPropertyChanged(nameof(CanOpenBookings));
        OnPropertyChanged(nameof(HasDetailActionEntries));
        OnPropertyChanged(nameof(HasDocumentOwnActions));
    }
}
