using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Messenger.Client;
using SimplArchive.Messenger.Client.Api;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// One conversation in the Messages tab's right pane (ADR 0967): its bubbles oldest first, read with the Messenger's own
/// rules — a cancelled key prompt is a decision (<see cref="KeyPrompt"/>), the newest message stays in view unless the
/// reader scrolled up (<see cref="ThreadAnchor"/>), an arrival reads it again (<see cref="ReloadOnArrival"/>), and who
/// else is typing shows beneath it — and what may be done with each bubble. Writing is its <see cref="Composer"/>'s.
/// One instance per conversation shown, as the phone builds one thread view per conversation: a conversation no longer
/// shown is <see cref="Close"/>d, so nothing it was still reading lands in the next one's pane (ADR 0661).
/// </summary>
/// <remarks>DEBT (ADR 0967): the read and the send follow the phone's <c>ThreadView</c>, whose orchestration still lives
/// in the MAUI screens; it moves into the Messenger client (SimplArchiveMessenger#34), and this copy goes then.</remarks>
public sealed partial class MessengerThreadViewModel : ObservableObject
{
    private readonly ChatSession _session;
    private readonly IStatusReporter _status;
    private readonly MessengerDialogs _dialogs;
    private readonly MessengerThreadHost _host;
    private readonly KeyPrompt _keyPrompt = new();
    private readonly ThreadAnchor _anchor = new();
    private readonly TypingIndicator _typers;
    private readonly ReloadOnArrival _arrivals;
    private IDisposable? _typingEvents;
    private Avalonia.Threading.DispatcherTimer? _typingTimer;
    private IReadOnlyList<ThreadRow> _rows = [];
    private List<(Conversation Conversation, IReadOnlyList<Message> Messages)> _read = [];   // sealed, as the server sent them
    private ThreadRead? _nextRead;
    private bool _readOnce;

    /// <param name="host">What the tab supplies to a thread: showing an opened file, leaving, and telling the list.</param>
    /// <param name="live">The Messenger's live events: who else is typing here.</param>
    public MessengerThreadViewModel(
        ChatSession session, Conversation conversation, IMessengerLiveEvents live, IStatusReporter status, MessengerDialogs dialogs, MessengerThreadHost host)
    {
        (_session, Conversation, _status, _dialogs, _host) = (session, conversation, status, dialogs, host);
        _typers = new TypingIndicator(session.Me);
        _arrivals = new ReloadOnArrival(conversation.Id, () => OnUi(() => LoadAsync(ReadReason.Arrival, quiet: true)));
        Composer = new MessengerComposerViewModel(this, session, status, dialogs);
        _typingEvents = live.Subscribe(HeardLive);
        if (Avalonia.Application.Current is not null)   // a running app ticks; a test drives Tick itself
        {
            _typingTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _typingTimer.Tick += (_, _) => Tick(DateTimeOffset.UtcNow);
            _typingTimer.Start();
        }
    }

    /// <summary>The conversation as last read: its links say what may be done in it.</summary>
    public Conversation Conversation { get; private set; }

    /// <summary>The page the next message is sent to; null until the thread was read.</summary>
    internal MessagePage? Page { get; private set; }

    internal ChatSession Session => _session;

    public MessengerComposerViewModel Composer { get; }

    public ObservableCollection<MessageBubbleViewModel> Bubbles { get; } = [];

    public string Title => ThreadRows.Title(Conversation);

    /// <summary>Whether the thread was let go of: what it reads after that is dropped.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>"Anna is typing…" beneath the messages; empty for nobody, and always where the user switched it off.</summary>
    [ObservableProperty] private string _typingLine = string.Empty;

    /// <summary>"New messages ↓": something arrived below a reader who scrolled up to older messages.</summary>
    [ObservableProperty] private bool _offersNew;

    /// <summary>Some message stayed sealed (the key prompt was cancelled, or no key was here): a click asks once more.</summary>
    [ObservableProperty] private bool _hasSealed;

    /// <summary>History — left or restored — is read, not written: where the composer would be, this says so.</summary>
    public bool IsHistory => Conversation.IsReadOnly;

    public bool CanWrite => !Conversation.IsReadOnly && !Conversation.Left;

    public string HistoryNote => Conversation.Left ? ViewText.Get("LeftReadOnly")
        : ViewText.Get("RestoredReadOnly", Conversation.RestoredFrom ?? string.Empty);

    /// <summary>Restored history whose members are all here: it can continue as a live conversation.</summary>
    public bool CanContinue => Conversation.Offers("continuation");

    /// <summary>A group's members, and what may be done with them; leaving is there too.</summary>
    public bool IsGroup => !Conversation.IsDirect && !Conversation.IsReadOnly;

    /// <summary>The view scrolls to the newest message when the thread says so.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>The thread is shown for the first time.</summary>
    public Task OpenAsync() => LoadAsync(ReadReason.Shown);

    /// <summary>Refresh: the user's own read, which may ask for the key again.</summary>
    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(ReadReason.Asked);

    /// <summary>A click on a sealed message: the key is asked for once more.</summary>
    [RelayCommand]
    public Task AskForKeyAsync()
    {
        CardModule.ForgetDeclines();
        return LoadAsync(ReadReason.Asked);
    }

    /// <summary>Something new arrived in <paramref name="arrivedIn"/> (null: somewhere the host cannot place).</summary>
    public void Arrived(Guid? arrivedIn) => _arrivals.Arrived(arrivedIn);

    /// <summary>The window left the front: arrivals wait, so no key prompt comes up behind the user's back.</summary>
    public void Paused() => _arrivals.Paused();

    /// <summary>The window is in front again: what arrived meanwhile is read now.</summary>
    public void Resumed() => _arrivals.Resumed();

    /// <summary>The reader scrolled; <paramref name="atEnd"/> when the newest message is in view.</summary>
    public void Scrolled(bool atEnd, bool byReader)
    {
        _anchor.Scrolled(atEnd ? _rows.Count - 1 : Math.Max(0, _rows.Count - 2), _rows.Count, byReader);
        OffersNew = _anchor.OffersNew;
    }

    /// <summary>The thread's height changed (the composer grew, the window was resized): at the end it stays there.</summary>
    public void Resized() => Scroll(_anchor.Resized());

    [RelayCommand]
    private void JumpToNew() => Scroll(_anchor.JumpToEnd());

    /// <summary>
    /// The key left the machine (the card was pulled): every opened conversation key is forgotten, and what the thread
    /// shows is redrawn SEALED from the messages as the server sent them — no request, no prompt — with a click to open
    /// them again, as the phone's guard does (core ADR 0931).
    /// </summary>
    public async Task KeyLeftAsync()
    {
        _session.Lock();
        _keyPrompt.Decline();
        var rows = new List<ThreadRow>();
        foreach (var (conversation, messages) in _read)
        {
            var opened = new List<OpenedMessage>();
            foreach (var message in messages)
            {
                opened.Add(await _session.OpenAsync(conversation, message, askForKey: false));   // keys gone: Locked
            }

            rows.AddRange(ThreadRows.Build(conversation, opened, _session.Me));
        }

        Show(rows);
        _host.ShowFile(null);   // an opened file goes too: its bytes were the key's
    }

    // Reads the conversation again. quiet: nobody asked (an arrival), so a failure is not put in front of anyone. A
    // cancelled key prompt is a decision (KeyPrompt): the messages show sealed, nothing asks again until the user does.
    internal async Task LoadAsync(ReadReason reason, bool quiet = false)
    {
        var cancelled = false;
        var began = false;
        var again = false;
        try
        {
            var mode = _keyPrompt.Begin(reason);
            if (mode == KeyAsk.Skip)
            {
                return;   // a read waiting for the key shows the thread when it is done
            }

            began = mode == KeyAsk.Ask;
            var ask = began;
            async Task<OpenedMessage> Open(Conversation conversation, Message message)
            {
                try
                {
                    return await _session.OpenAsync(conversation, message, ask);   // the first asks for the key, once
                }
                catch (OperationCanceledException e) when (ask && e is not TaskCanceledException)
                {
                    (cancelled, ask) = (true, false);
                }
                catch (EnvelopeNotOpenedException missing) when (ask)
                {
                    // No key here that opens it: shown sealed, with why in the status line, never an empty thread.
                    (cancelled, ask) = (true, false);
                    _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgKeyMissing"), missing.Message));
                }

                return await _session.OpenAsync(conversation, message, askForKey: false);
            }

            var conversation = await _session.Api.RefreshAsync(Conversation);
            if (IsClosed)
            {
                return;   // let go of while it was read: nothing of it shows, nothing is marked read
            }

            SetConversation(conversation);
            var read = new List<(Conversation, IReadOnlyList<Message>)>();
            var rows = new List<ThreadRow>();

            // Restored history this conversation continues comes first, each opened under the ids it was sealed with.
            if (conversation.Offers("earlier"))
            {
                foreach (var earlier in (await _session.Api.EarlierAsync(conversation)).Items)
                {
                    var (_, old) = await _session.MessagesAsync(earlier);
                    var openedOld = new List<OpenedMessage>();
                    foreach (var message in old)
                    {
                        openedOld.Add(await Open(earlier, message));
                    }

                    read.Add((earlier, old));
                    rows.AddRange(ThreadRows.Build(earlier, openedOld, _session.Me));
                }
            }

            var (first, messages) = await _session.MessagesAsync(conversation);
            var opened = new List<OpenedMessage>();
            foreach (var message in messages)
            {
                opened.Add(await Open(conversation, message));
            }

            read.Add((conversation, messages));
            rows.AddRange(ThreadRows.Build(conversation, opened, _session.Me));
            if (IsClosed)
            {
                return;
            }

            (Page, _read) = (first, read);
            Show(rows);

            // A sealed newest message is not read: the phone's signal stays until the user has actually seen it.
            if (opened.Count > 0 && opened[^1].State != MessageState.Locked)
            {
                await _session.Api.MarkReadAsync(conversation, messages[^1].SentAt);
                _host.ListChanged();   // its unread count is gone from the list beside it
            }
        }
        catch (Exception e) when (!quiet && MessengerRefusal.IsReported(e))
        {
            DesktopLog.Warn(e, "Messages: the conversation could not be read");
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgThreadFailed"), MessengerRefusal.Wording(e)));
        }
        finally
        {
            again = began && _keyPrompt.End(cancelled);
        }

        if (again && !IsClosed)
        {
            await LoadAsync(ReadReason.Shown, quiet: true);   // something wanted a read while this one waited for the key
        }
    }

    /// <summary>The caller sent something: their message is shown, read back from the server.</summary>
    internal Task SentAsync()
    {
        _nextRead = ThreadRead.Sent;
        return LoadAsync(ReadReason.Asked);
    }

    /// <summary>A message is on its way: its "Sending…" bubble at the end until the server answers.</summary>
    internal void ShowSending(string text)
    {
        Bubbles.Add(new MessageBubbleViewModel(ThreadRows.Sending(text, _session.Me), this));
        Scroll(ThreadScroll.ToEnd);
    }

    /// <summary>The send failed or was cancelled: the bubble on its way goes, the thread is as it was.</summary>
    internal void SendingFailed() => Show(_rows, ThreadRead.Reloaded);

    /// <summary>A cancelled key prompt while sending: not asked again by itself.</summary>
    internal void KeyDeclined() => _keyPrompt.Decline();

    private void Show(IReadOnlyList<ThreadRow> rows, ThreadRead? why = null)
    {
        _rows = rows;
        Bubbles.Clear();
        foreach (var row in rows)
        {
            Bubbles.Add(new MessageBubbleViewModel(row, this));
        }

        HasSealed = rows.Any(r => r.IsLocked);
        var reason = why ?? _nextRead ?? (_readOnce ? ThreadRead.Reloaded : ThreadRead.Opened);
        (_nextRead, _readOnce) = (null, true);
        Scroll(_anchor.RowsSet(reason, rows.Count > 0 ? rows[^1].Message.Id : null));
    }

    private void Scroll(ThreadScroll scroll)
    {
        OffersNew = _anchor.OffersNew;
        if (scroll == ThreadScroll.ToEnd)
        {
            ScrollToEndRequested?.Invoke();
        }
    }

    private void SetConversation(Conversation conversation)
    {
        Conversation = conversation;
        OnPropertyChanged(nameof(Conversation));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsHistory));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(HistoryNote));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(IsGroup));
        ShowTypers();   // the user may have switched the indicator off meanwhile: then nobody's typing shows
    }

    // ---- What may be done with a bubble: its context menu, as the phone's tap offers it ----

    [RelayCommand]
    private void Reply(MessageBubbleViewModel? bubble)
    {
        if (bubble is { CanReply: true })
        {
            Composer.StartReply(bubble.Row, bubble.Row.SenderName ?? (bubble.IsMine ? ViewText.Get("You") : Title));
        }
    }

    [RelayCommand]
    private void Edit(MessageBubbleViewModel? bubble)
    {
        if (bubble is { CanEdit: true })
        {
            Composer.StartEdit(bubble.Row);
        }
    }

    [RelayCommand]
    private Task DeleteForEveryoneAsync(MessageBubbleViewModel? bubble) =>
        bubble is { CanDeleteForEveryone: true } ? ActAsync(() => _session.Api.DeleteForEveryoneAsync(bubble.Row.Message)) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteForMeAsync(MessageBubbleViewModel? bubble) =>
        bubble is { CanDeleteForMe: true } ? ActAsync(() => _session.Api.HideForMeAsync(bubble.Row.Message)) : Task.CompletedTask;

    [RelayCommand]
    private Task ShowReadByAsync(MessageBubbleViewModel? bubble) =>
        bubble is { CanShowReadBy: true } ? _dialogs.InformAsync(bubble.Receipt ?? ViewText.Get("WhoRead"), string.Join("\n", bubble.Row.ReadBy)) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyAsync(MessageBubbleViewModel? bubble) =>
        bubble is { CanCopy: true } && _dialogs.Copy is { } copy ? copy(bubble.Row.Text) : Task.CompletedTask;

    /// <summary>Opens one of a bubble's files, with the key, in the app wherever it can show it (#27).</summary>
    [RelayCommand]
    private async Task OpenFileAsync(MessageFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            var content = await _session.OpenFileAsync(file.Bubble.Row.Message, file.Index);
            if (!IsClosed)
            {
                _host.ShowFile(await MessengerFileViewModel.OpenAsync(content, file.File, Conversation.FilesStayInApp, _dialogs, _status, () => _host.ShowFile(null)));
            }
        }
        catch (OperationCanceledException e) when (e is not TaskCanceledException)
        {
            // The key prompt was cancelled: the user's decision, nothing to report.
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            DesktopLog.Warn(e, "Messages: a sent file could not be opened");
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgFileOpenFailed"), MessengerRefusal.Wording(e)));
        }
    }

    /// <summary>Restored history continues here: the live conversation replaces this thread, the history above it.</summary>
    [RelayCommand]
    private async Task ContinueHereAsync()
    {
        try
        {
            _host.ShowConversation(await _session.ContinueAsync(Conversation));
        }
        catch (ContinuationBlockedException blocked)
        {
            await _dialogs.InformAsync(ViewText.Get("ContinueHere"), ViewText.Get("ContinueMissing", string.Join(", ", blocked.Missing)));
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            Failed(e);
        }
    }

    [RelayCommand]
    private void ShowMembers() => _host.ShowMembers(Conversation);

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (!await _dialogs.ConfirmAsync(ViewText.Get("Leave"), ViewText.Get("LeaveConfirm"), ViewText.Get("Leave")))
        {
            return;
        }

        try
        {
            await _session.LeaveAsync(Conversation);
            _host.Left();
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            Failed(e);
        }
    }

    // An act on a message, then the thread read again; a refusal in the status line, in words a person can act on.
    private async Task ActAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            Failed(e);
        }

        await LoadAsync(ReadReason.Asked);
    }

    internal void Failed(Exception e)
    {
        DesktopLog.Warn(e, "Messages: an act in the conversation failed");
        _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgActFailed"), MessengerRefusal.Wording(e)));
    }

    // ---- Who else is typing (#26), off the window's live connection ----

    private void HeardLive(string name, string payload)
    {
        if (TypingEvent.Parse(name, payload) is { } heard && heard.Conversation == Conversation.Id)
        {
            PostToUi(() => Heard(heard));
        }
    }

    /// <summary>A typing event for this conversation arrived.</summary>
    public void Heard(TypingEvent heard)
    {
        if (!IsClosed && TypingRule.Shows(Conversation) && _typers.Received(heard.User, heard.Typing, DateTimeOffset.UtcNow))
        {
            ShowTypers();
        }
    }

    // Once a second: the user's own idle stop, and the typers' expiry.
    internal void Tick(DateTimeOffset now)
    {
        Composer.Tick(now);
        if (_typers.Expire(now))
        {
            ShowTypers();
        }
    }

    private void ShowTypers()
    {
        if (!TypingRule.Shows(Conversation))
        {
            _typers.Clear();
        }

        TypingLine = _typers.Line(Conversation) ?? string.Empty;
    }

    /// <summary>The thread is no longer shown: it stops listening and reading, and tells the others the user stopped typing.</summary>
    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        _arrivals.Stop();
        _typingEvents?.Dispose();
        _typingEvents = null;
        _typingTimer?.Stop();
        _typingTimer = null;
        Composer.Closed();
        _typers.Clear();
        TypingLine = string.Empty;
    }

    // An arrival's read runs off the UI thread's timer: the bubbles are the UI thread's.
    // Without a running app (a test) there is no UI thread to wait for.
    internal static Task OnUi(Func<Task> read) =>
        Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            ? read()
            : Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(read);

    internal static void PostToUi(Action act)
    {
        if (Avalonia.Application.Current is null)
        {
            act();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(act);
        }
    }

    /// <summary>Demo rows for the headless screenshot: drawn through the same rules as a real thread.</summary>
    internal void ShowForScreenshot(IReadOnlyList<ThreadRow> rows) => Show(rows, ThreadRead.Opened);
}

/// <summary>
/// What the Messages tab supplies to the thread it shows (ADR 0730: the shell's answers are constructor arguments): where
/// an opened file is shown, what showing another conversation, a group's members or leaving does, and telling the list.
/// </summary>
public sealed class MessengerThreadHost(
    Action<MessengerFileViewModel?> showFile, Action<Conversation> showConversation, Action<Conversation> showMembers, Action left, Action listChanged)
{
    public void ShowFile(MessengerFileViewModel? file) => showFile(file);

    public void ShowConversation(Conversation conversation) => showConversation(conversation);

    public void ShowMembers(Conversation group) => showMembers(group);

    public void Left() => left();

    public void ListChanged() => listChanged();
}
