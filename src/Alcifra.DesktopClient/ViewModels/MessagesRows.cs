using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.Localization;
using Alcifra.Messenger.Client;
using Alcifra.Messenger.Client.Api;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A conversation in the list: its title, what kind it is (a group and its member count, or history), and its unread
/// count as a badge — the phone's row (ADR 0967). A reload updates the row it already shows rather than replacing it
/// (<see cref="RowSync"/>): replacing the selected row deselected it, which closed the open conversation and its draft.
/// </summary>
public sealed partial class MessagesConversationViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Kind), nameof(HasKind), nameof(Unread), nameof(HasUnread))]
    private Conversation _conversation = null!;   // set by every construction (MessagesTabViewModel, its demo)

    public string Title => ThreadRows.Title(Conversation);

    /// <summary>Beneath the title: history says which, a group says "Group · 3 members", a chat says nothing.</summary>
    public string Kind => Conversation.Left ? ViewText.Get("LeftMark")
        : Conversation.Restored ? ViewText.Get("RestoredMark")
        : Conversation.IsDirect ? string.Empty
        : ViewText.Get("GroupMembers", Conversation.Members.Count);

    public bool HasKind => Kind.Length > 0;

    public string Unread => Conversation.Unread.ToString(CultureInfo.CurrentCulture);

    public bool HasUnread => Conversation.Unread > 0;
}

/// <summary>One file a bubble carries, opened on its own (each with the key, one use per file).</summary>
public sealed class MessageFileViewModel(MessageBubbleViewModel bubble, int index, SentFile file)
{
    public MessageBubbleViewModel Bubble { get; } = bubble;

    public int Index { get; } = index;

    public SentFile File { get; } = file;

    public string Label => $"📎 {File.Name}";
}

/// <summary>
/// One bubble as the desktop draws it, from the row the phone draws too (the Messenger's display rules), and what may be
/// done with it — offered in its context menu, as the phone offers it on a tap.
/// </summary>
public sealed class MessageBubbleViewModel
{
    public MessageBubbleViewModel(ThreadRow row, MessengerThreadViewModel? thread = null)
    {
        (Row, Thread) = (row, thread);
        Files = [.. row.Files.Select((f, i) => new MessageFileViewModel(this, i, f))];
    }

    public ThreadRow Row { get; }

    /// <summary>The thread it is in: its context menu's commands, bound from the bubble (a popup is outside the list).</summary>
    public MessengerThreadViewModel? Thread { get; }

    public bool IsMine => Row.IsMine;

    public string? Sender => Row.SenderName;

    public bool HasSender => Row.SenderName is not null;

    /// <summary>The text; a sealed message says it opens with a click, the desktop's word for the phone's tap.</summary>
    public string Text => Row.IsLocked ? Strings.Get("MsgLockedClick") : Row.Text;

    public bool HasText => Row.Files.Count == 0 || Row.Text.Length > 0;

    public bool IsPlaceholder => Row.IsPlaceholder;

    public bool IsLocked => Row.IsLocked;

    public bool IsSending => Row.IsSending;

    public string? Quote => Row.Quote;

    public bool HasQuote => Row.Quote is not null;

    /// <summary>Every file the message carries, each on its own line and opened on its own.</summary>
    public IReadOnlyList<MessageFileViewModel> Files { get; }

    public bool HasFiles => Files.Count > 0;

    public string Footer => Row.Footer;

    public string? Receipt => Row.Receipt;

    public bool HasReceipt => Row.Receipt is not null;

    public bool CanReply => Row.CanReply && !Row.IsSending;

    public bool CanEdit => Row.CanEdit && !Row.IsSending;

    public bool CanDeleteForEveryone => Row.CanDeleteForEveryone && !Row.IsSending;

    /// <summary>Hiding is offered for every message the server has: it touches nobody else's view.</summary>
    public bool CanDeleteForMe => !Row.IsSending && Row.Message.Offers("hide");

    public bool CanShowReadBy => Row.ReadBy.Count > 0;

    public bool CanCopy => !Row.IsPlaceholder && Row.Text.Length > 0;

    public bool HasActions => CanReply || CanEdit || CanDeleteForEveryone || CanDeleteForMe || CanShowReadBy || CanCopy;
}

/// <summary>A chip above the composer: a file waiting to go with the next send, removable with ✕.</summary>
public sealed class DraftChipViewModel(DraftFile file)
{
    public DraftFile File { get; } = file;

    public string Label => File.Chip;

    public string RemoveTip => ViewText.Get("RemoveFile", File.File.Name);
}

/// <summary>A file the user chose to attach, as the view's picker hands it over: its name and its bytes.</summary>
public sealed record PickedFile(string Name, byte[] Content);

/// <summary>
/// The dialogs the Messages tab's panes ask the view for (ADR 0730: what the view supplies stays a callback): each is
/// set by <c>MessagesTab</c> when it binds, and an unset one makes its action do nothing, visibly.
/// </summary>
public sealed class MessengerDialogs
{
    /// <summary>A yes/no question: title, message and the confirming button's label; true when confirmed.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>A message to read and close: title and text.</summary>
    public Func<string, string, Task>? Inform { get; set; }

    /// <summary>The file picker for attachments (several at once); empty when cancelled.</summary>
    public Func<Task<IReadOnlyList<PickedFile>>>? PickFiles { get; set; }

    /// <summary>A save dialog: a stream for the suggested file name, or null when cancelled.</summary>
    public Func<string, Task<Stream?>>? SaveFile { get; set; }

    /// <summary>Puts text on the clipboard.</summary>
    public Func<string, Task>? Copy { get; set; }

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm) =>
        Confirm is not null && await Confirm(title, message, confirm);

    internal Task InformAsync(string title, string message) => Inform?.Invoke(title, message) ?? Task.CompletedTask;
}

/// <summary>A refusal or a failure in words a person can act on: a stale edit and the last administrator say so plainly.</summary>
internal static class MessengerRefusal
{
    // DEBT (ADR 0967): the same wording as the phone screens' MessengerScreens.ShowAsync, which keeps it inside the MAUI
    // package; it moves to Alcifra.Messenger.Client with the thread's read and send orchestration
    // (SimplArchiveMessenger#34), and this copy goes then.
    public static string Wording(Exception exception) => exception switch
    {
        MessengerApiException { Code: "ETAG_MISMATCH" } => ViewText.Get("ChangedElsewhere"),
        MessengerApiException { Code: "MESSENGER_LAST_ADMIN" } => ViewText.Get("LastAdmin"),
        _ => exception.Message,
    };

    /// <summary>The failures an act in the Messages tab reports rather than throws.</summary>
    public static bool IsReported(Exception e) => e is HttpRequestException or MessengerApiException or EnvelopeNotOpenedException
        or IOException or UnauthorizedAccessException or TaskCanceledException or ContinuationBlockedException
        or System.Security.Cryptography.CryptographicException;
}
