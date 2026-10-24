using SimplArchive.Client.Hypermedia;
using SimplArchive.Client.Models;

namespace SimplArchive.Client.Components.Panes;

/// <summary>
/// The document's own actions that are not its details (ADR 0970) — what the ribbon's Documents group and a row's
/// ⋮ menu offer for ONE subject, after they left the details pane.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than a dozen parameters, and a RECORD so a component receiving it re-renders: Blazor cannot tell
/// whether a complex parameter changed and so always re-renders on one, which is what DocumentActionsRow (ADR 0664)
/// had to learn the hard way when its only parameters were cached method-group callbacks.
/// </para>
/// <para>
/// Built by the shell for the subject the details pane describes — <see cref="None"/> for any other row, so a ⋮
/// menu opened on a row that is not the selection offers none of these rather than the selection's (ADR 0559).
/// </para>
/// </remarks>
public sealed record DocumentActionOffer(
    bool CanFollow,
    bool Subscribed,
    bool CanShare,
    IReadOnlyList<string> WorkflowSteps,
    bool CanBook,
    IReadOnlyList<LinkResponse> GenericActions,
    IReadOnlyList<ModuleActionDto> ModuleActions,
    bool Busy)
{
    public static DocumentActionOffer None { get; } = new(false, false, false, [], false, [], [], false);

    /// <summary>The resource key for a workflow step — the workflow DIALOG's own labels, so the same action reads
    /// identically wherever it is offered. An unknown rel falls back to the status label, never a raw rel.</summary>
    public static string WorkflowStepLabelKey(string rel) => rel switch
    {
        "submit" => "WorkflowSubmit",
        "approve" => "WorkflowApprove",
        "reject" => "WorkflowReject",
        "reassign" => "WorkflowReassign",
        "release" => "WorkflowRelease",
        _ => "WorkflowStatus",
    };

    /// <summary>Bookings… and the server-labeled actions — the one "Actions" submenu both surfaces show.</summary>
    public bool HasServerActions => CanBook || GenericActions.Count > 0 || ModuleActions.Count > 0;
}

/// <summary>What the document actions DO — implemented by the shell, which owns the subject and the dialogs.</summary>
/// <remarks>An interface for the IPreviewAnnotationHost reason: one cohesive contract, always wired together.</remarks>
public interface IDocumentActionHost
{
    Task ToggleFollowAsync();

    Task ShareAsync();

    /// <summary>A workflow step (#691): approve/submit/release act; reject and reassign open the dialog that asks.</summary>
    Task RunWorkflowStepAsync(string rel);

    Task OpenBookingsAsync();

    Task RunGenericActionAsync(LinkResponse action);

    Task RunModuleActionAsync(ModuleActionDto action);
}
