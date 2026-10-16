namespace SimplArchive.ModuleAbi;

/// <summary>
/// A notice a module sends from an ACTION rather than from the escalation sweep (ABI 1.10, core ADR 0902): a renewal
/// confirmation, with the signed licence attached. Inject it into a controller.
/// </summary>
/// <remarks>
/// <para>
/// A user of the tenant gets an in-app notification (and mail, as their settings say); any other address is mailed only
/// if the module declares <see cref="IIndustryModule.SendsExternalNotices"/>, and only when the tenant can send mail.
/// </para>
/// <para>
/// The notice is QUEUED, not sent: it is written in the current transaction and leaves after the commit, so an act that
/// rolls back sends nothing (core ADR 0794). Wrap the act in <see cref="IModuleArchiveFacade.InTransactionAsync"/>.
/// </para>
/// </remarks>
public interface IModuleNotices
{
    /// <summary>Queues <paramref name="notice"/>; false when it was refused (an undeclared external address, or no mail
    /// configured), which the host logs at Warning.</summary>
    Task<bool> QueueAsync(ModuleNotice notice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells a user that something new waits in <see cref="ModuleSignal.DocumentId"/> (ABI 1.22, core ADR 0943): a
    /// conversation with new messages. Never mailed, and COALESCED: while the user's signal about that document is unread,
    /// another one refreshes it instead of adding a row, so the phone is pushed once and the live list counts up. Only
    /// about a document wearing one of the calling module's own masks; the acting user is never signalled.
    /// </summary>
    /// <remarks>Default-implemented so a module's 1.21 test double still compiles; the host implements it.</remarks>
    Task SignalAsync(ModuleSignal signal, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not implement the ABI 1.22 SignalAsync.");

    /// <summary>
    /// Marks <paramref name="recipientUserId"/>'s unread signals about <paramref name="documentId"/> read (ABI 1.22): the
    /// user has caught up, so the next signal pushes the phone again. Only for a document of the calling module's own.
    /// </summary>
    /// <remarks>Default-implemented so a module's 1.21 test double still compiles; the host implements it.</remarks>
    Task ClearSignalsAsync(Guid recipientUserId, Guid documentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not implement the ABI 1.22 ClearSignalsAsync.");
}

/// <summary>
/// What a signal says (ABI 1.22). Shown as the notification's title and body, and on a phone's lock screen — so it carries
/// no content a member would not want there: the Messenger says "New messages", never the sender or the text.
/// </summary>
public sealed record ModuleSignal(Guid RecipientUserId, Guid DocumentId, string Title, string Body);

/// <summary>A notice from an action: who, what, and optionally where replies go and a document to attach.</summary>
public sealed record ModuleNotice(string RecipientEmail, string Subject, string Message)
{
    /// <summary>Where a reply should go (external mail only).</summary>
    public string? ReplyTo { get; init; }

    /// <summary>A document whose current content is attached, frozen at queue time (external mail only).</summary>
    public Guid? AttachmentDocumentId { get; init; }
}
