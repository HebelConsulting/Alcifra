using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Messenger.Client;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// The thread's compose bar (ADR 0967), with the phone's rules from the Messenger client: ONE send at a time
/// (<see cref="SendGate"/>) shown as a "Sending…" bubble, files as chips above the field sent with the text as their
/// caption in one message (<see cref="ComposerDraft"/>), a reply's quote bar, and the user's own "is typing"
/// (<see cref="TypingSignal"/>). Editing one's own message happens HERE, in place of the text — its Save and ✕ at the
/// field that started it (ADR 0550); Esc cancels a reply or an edit, Enter sends, Shift+Enter is a new line.
/// </summary>
public sealed partial class MessengerComposerViewModel : ObservableObject
{
    private readonly MessengerThreadViewModel _thread;
    private readonly ChatSession _session;
    private readonly IStatusReporter _status;
    private readonly MessengerDialogs _dialogs;
    private readonly SendGate _gate = new();
    private readonly ComposerDraft _draft = new();
    private readonly TypingSignal _typing = new();
    private ThreadRow? _replyTo;
    private ThreadRow? _editing;

    internal MessengerComposerViewModel(MessengerThreadViewModel thread, ChatSession session, IStatusReporter status, MessengerDialogs dialogs)
    {
        (_thread, _session, _status, _dialogs) = (thread, session, status, dialogs);
        _gate.Changed += () => MessengerThreadViewModel.PostToUi(() => OnPropertyChanged(nameof(IsSending)));
        _draft.Changed += () => MessengerThreadViewModel.PostToUi(DrawChips);
    }

    /// <summary>What is typed; read-only while a message is on its way.</summary>
    [ObservableProperty] private string _text = string.Empty;

    /// <summary>"Replying to Anna" above the field, or "Editing" while an own message is changed; empty for neither.</summary>
    [ObservableProperty] private string _barLine = string.Empty;

    [ObservableProperty] private bool _isEditing;

    /// <summary>The files waiting as chips above the field.</summary>
    public ObservableCollection<DraftChipViewModel> Chips { get; } = [];

    public bool HasChips => Chips.Count > 0;

    public bool HasBar => BarLine.Length > 0;

    /// <summary>A message is on its way: Send, Attach and the field wait until the server answers.</summary>
    public bool IsSending => _gate.Sending;

    /// <summary>The Send button's label: Save while editing.</summary>
    public string SendLabel => IsEditing ? Strings.Get("MsgSaveEdit") : ViewText.Get("Send");

    partial void OnBarLineChanged(string value) => OnPropertyChanged(nameof(HasBar));

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(SendLabel));

    // "Is typing" (#26): a keystroke may start or refresh it.
    partial void OnTextChanged(string value)
    {
        if (!IsEditing)
        {
            Signal(_typing.Typed(DateTimeOffset.UtcNow, !string.IsNullOrWhiteSpace(value)));
        }
    }

    internal void StartReply(ThreadRow row, string to)
    {
        CancelEdit();
        _replyTo = row;
        BarLine = ViewText.Get("ReplyingTo", to);
    }

    internal void StartEdit(ThreadRow row)
    {
        _replyTo = null;
        (_editing, IsEditing) = (row, true);
        Text = row.EditableText ?? string.Empty;
        BarLine = ViewText.Get("Edit");
    }

    /// <summary>✕ or Esc: the reply or the edit goes; an edit's text with it, a reply leaves what was typed.</summary>
    [RelayCommand]
    public void CancelBar()
    {
        if (IsEditing)
        {
            CancelEdit();
            Text = string.Empty;
        }

        _replyTo = null;
        BarLine = string.Empty;
    }

    private void CancelEdit() => (_editing, IsEditing) = (null, false);

    /// <summary>
    /// Send (the button, Enter, or Ctrl/Cmd+S while editing): one message, the chips' files with the text as their caption
    /// or the text alone — or the edit saved. A send asked for while one is on its way is not made; answered, the field
    /// empties; refused or failed, the text and the chips stay, to be sent again, never sent twice by themselves.
    /// </summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        var text = Text.Trim();
        if (IsEditing)
        {
            await SaveEditAsync(text);
            return;
        }

        var what = _draft.What(text);
        if (what == DraftSend.Nothing || _thread.Page is not { } page || _gate.Sending || !_thread.CanWrite)
        {
            return;
        }

        Signal(_typing.Stopped());   // what was typed is on its way: no longer typing
        var replyTo = _replyTo?.Message.Id;
        var sent = false;
        try
        {
            sent = await _gate.SendAsync(async () =>
            {
                var shown = what == DraftSend.Files
                    ? string.Join(" · ", _draft.Files.Select(f => $"📎 {f.File.Name}").Append(text).Where(t => t.Length > 0))
                    : text;
                _thread.ShowSending(shown);
                if (what == DraftSend.Files)
                {
                    await _session.SendFilesAsync(_thread.Conversation, page, _draft.Outgoing, text, replyTo);
                }
                else
                {
                    await _session.SendAsync(_thread.Conversation, page, text, replyTo);
                }

                Text = string.Empty;
                _draft.Clear();
                CancelBar();
            });
        }
        catch (OperationCanceledException e) when (e is not TaskCanceledException)
        {
            _thread.SendingFailed();   // the key prompt was cancelled: a decision, not a failure, and not asked again
            _thread.KeyDeclined();
            return;
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _thread.SendingFailed();   // not sent: the bubble on its way goes, the text and the chips are still there
            DesktopLog.Warn(e, "Messages: a message could not be sent");
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgSendFailed"), MessengerRefusal.Wording(e)));
            return;
        }

        if (sent)
        {
            await _thread.SentAsync();
        }
    }

    private async Task SaveEditAsync(string text)
    {
        if (_editing is not { } row || text.Length == 0 || _gate.Sending)
        {
            return;
        }

        try
        {
            await _gate.SendAsync(async () =>
            {
                await _session.EditAsync(_thread.Conversation, row.Message, text);
                CancelBar();
            });
        }
        catch (OperationCanceledException e) when (e is not TaskCanceledException)
        {
            _thread.KeyDeclined();
            return;
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _thread.Failed(e);   // a stale edit: "changed meanwhile on another device", and shown as it is now
        }

        await _thread.RefreshAsync();
    }

    /// <summary>Attach: the chosen files join the draft as chips (several at once); Send sends them all as one message.</summary>
    [RelayCommand]
    public Task AttachAsync() => _dialogs.PickFiles is { } pick ? AddFilesAsync(pick) : Task.CompletedTask;

    /// <summary>
    /// Whether files may join the draft now: not while a send is on its way, nor while a message is edited (a message
    /// with files is not edited). Attach and a drop on the conversation (#2037) both ask it; a drop shows "no" when not.
    /// </summary>
    public bool AcceptsFiles => !_gate.Sending && !IsEditing;

    /// <summary>
    /// Files join the draft as chips, read by <paramref name="read"/>: the picker's choice (Attach) or the files dropped on
    /// the conversation (#2037). Nothing is sent until Send; the first refusal (too large, too many) is reported.
    /// </summary>
    public async Task AddFilesAsync(Func<Task<IReadOnlyList<PickedFile>>> read)
    {
        if (!AcceptsFiles)
        {
            return;
        }

        try
        {
            foreach (var file in await read())
            {
                if (Add(file) is { } refused)
                {
                    _status.Report(refused);
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgAttachFailed"), e.Message));
        }
    }

    /// <summary>A file joins the draft; null when it did, else why not (too large, too many).</summary>
    public string? Add(PickedFile file) =>
        _draft.Add(new OutgoingFile(file.Content, file.Name, DocumentsClient.GuessContentType(file.Name)));

    [RelayCommand]
    private void RemoveChip(DraftChipViewModel? chip)
    {
        if (chip is not null && !_gate.Sending)
        {
            _draft.Remove(chip.File.Id);
        }
    }

    private void DrawChips()
    {
        Chips.Clear();
        foreach (var file in _draft.Files)
        {
            Chips.Add(new DraftChipViewModel(file));
        }

        OnPropertyChanged(nameof(HasChips));
    }

    /// <summary>The field lost the focus: no longer typing.</summary>
    public void LeftField() => Signal(_typing.Stopped());

    internal void Tick(DateTimeOffset now) => Signal(_typing.Tick(now));

    internal void Closed() => Signal(_typing.Stopped());

    // Tells the others, best effort: a typing state lost on the way is worth nothing a few seconds later anyway.
    private void Signal(TypingSend send)
    {
        if (send == TypingSend.Nothing || !TypingRule.Sends(_thread.Conversation))
        {
            return;
        }

        var conversation = _thread.Conversation;
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.Api.TypingAsync(conversation, send == TypingSend.Start);
            }
            catch (Exception e) when (MessengerRefusal.IsReported(e))
            {
                DesktopLog.Debug("Messages: the typing signal was not delivered: {Reason}", e.Message);
            }
        });
    }
}
