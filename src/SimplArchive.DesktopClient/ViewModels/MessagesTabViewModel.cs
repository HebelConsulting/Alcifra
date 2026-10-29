using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Messenger.Client;
using SimplArchive.Messenger.Client.Api;
using SimplArchive.Messenger.Client.Backup;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>What the Messages tab's right pane shows.</summary>
public enum MessagesPane
{
    /// <summary>Nothing selected: "Select a conversation".</summary>
    None,

    Thread,

    /// <summary>A sent file, opened in the app over the thread.</summary>
    File,

    /// <summary>The people picker: a new chat, a new group, or a member to add.</summary>
    People,

    /// <summary>A group's members.</summary>
    Members,
}

/// <summary>
/// The Messages tab (core #1813; full parity with the phone's Messenger, ADR 0967): every conversation of the user, read
/// AND written here — send, reply, edit, delete, files as chips, new chats and groups, members, leaving, continuing
/// restored history — with Back up and Restore in the ribbon. Shown only while the archive offers the Messenger to this
/// user. The rules are the Messenger client's own (<c>Display</c>), so a bubble reads, and an act behaves, as on the phone.
/// </summary>
public sealed partial class MessagesTabViewModel : ObservableObject
{
    private readonly IShellContext _shell;
    private readonly Func<SimplArchiveApiClient?> _api;
    private readonly IMessengerLiveEvents _live;
    private readonly Func<string?> _email;
    private ChatSession? _session;
    private SimplArchiveApiClient? _boundTo;   // the client the session was built over
    private ReloadOnArrival? _listArrivals;
    private ConversationList? _list;
    private MessengerPreferences? _preferences;
    private bool _inFront = true;

    /// <param name="api">The signed-in client, read when needed: the tab is built before sign-in.</param>
    /// <param name="live">The Messenger's events off the window's own live connection (SimplArchiveMessenger #26): who is
    /// typing in the thread on show. ADR 0730: what the shell supplies is a constructor argument.</param>
    /// <param name="email">The signed-in user's e-mail: how a new chat or group finds the user's own certificates.</param>
    public MessagesTabViewModel(IShellContext shell, Func<SimplArchiveApiClient?> api, IMessengerLiveEvents live, Func<string?> email) =>
        (_shell, _api, _live, _email) = (shell, api, live, email);

    public ObservableCollection<MessagesConversationViewModel> Conversations { get; } = [];

    /// <summary>The dialogs the panes ask the view for (ADR 0730), set by the view when it binds.</summary>
    public MessengerDialogs Dialogs { get; } = new();

    /// <summary>The archive offers the Messenger to this user: what shows the tab.</summary>
    [ObservableProperty] private bool _isOffered;

    [ObservableProperty] private MessagesConversationViewModel? _selected;

    [ObservableProperty] private string _emptyMessage = string.Empty;

    [ObservableProperty] private MessagesPane _pane;

    /// <summary>The conversation on show; null when none is selected.</summary>
    [ObservableProperty] private MessengerThreadViewModel? _thread;

    /// <summary>A sent file opened over the thread.</summary>
    [ObservableProperty] private MessengerFileViewModel? _file;

    [ObservableProperty] private MessengerPeopleViewModel? _people;

    [ObservableProperty] private MessengerMembersViewModel? _members;

    /// <summary>Whether the user's "is typing" goes both ways (SimplArchiveMessenger #26); null while the server offers no preference.</summary>
    [ObservableProperty] private bool? _typingIndicator;

    public bool OffersTypingPreference => TypingIndicator is not null;

    public bool ShowsNothing => Pane == MessagesPane.None;

    public bool ShowsThread => Pane == MessagesPane.Thread;

    public bool ShowsFile => Pane == MessagesPane.File;

    public bool ShowsPeople => Pane == MessagesPane.People;

    public bool ShowsMembers => Pane == MessagesPane.Members;

    partial void OnPaneChanged(MessagesPane value)
    {
        OnPropertyChanged(nameof(ShowsNothing));
        OnPropertyChanged(nameof(ShowsThread));
        OnPropertyChanged(nameof(ShowsFile));
        OnPropertyChanged(nameof(ShowsPeople));
        OnPropertyChanged(nameof(ShowsMembers));
    }

    public string SelectConversationLine => ViewText.Get("SelectConversation");

    /// <summary>The view's save dialog for a backup (ADR 0730: the view's), the same one an opened file's Save as… uses.</summary>
    public Func<string, Task<Stream?>>? SaveFile
    {
        get => Dialogs.SaveFile;
        set => Dialogs.SaveFile = value;
    }

    /// <summary>The view's open dialog: the chosen backup, or null when cancelled.</summary>
    public Func<Task<Stream?>>? OpenFile { get; set; }

    partial void OnTypingIndicatorChanged(bool? value) => OnPropertyChanged(nameof(OffersTypingPreference));

    /// <summary>
    /// Asks the archive whether it offers the Messenger here; the tab shows only if it does. Run at every sign-in,
    /// including the one after an ended session and impersonation: the Messenger client is built over THAT session's
    /// client, and a session built over an earlier one is never used again (see <see cref="Current"/>).
    /// </summary>
    public async Task DiscoverAsync()
    {
        var api = _api();
        Forget();   // the previous session's keys and rows go now, whatever this discovery finds
        if (api is null)
        {
            IsOffered = false;
            return;
        }

        ChatSession? session = null;
        try
        {
            var messenger = new MessengerApi(api.Core.Http);
            if (await messenger.DiscoverAsync() && await new WhoAmIClient(api.Core).GetAsync() is { UserId: { } me } who)
            {
                session = new ChatSession(messenger, new ConversationKeyOpener(), me, _email() is { Length: > 0 } email ? email : who.UserName ?? string.Empty);
            }
        }
        catch (Exception e) when (e is HttpRequestException or MessengerApiException or TaskCanceledException)
        {
            DesktopLog.Debug("The Messenger could not be discovered; no Messages tab: {Reason}", e.Message);
        }

        if (!ReferenceEquals(api, _api()))
        {
            session?.Lock();   // a newer sign-in arrived meanwhile; its own discovery decides
            return;
        }

        _session?.Lock();
        (_session, _boundTo, IsOffered) = (session, api, session is not null);
        if (session is not null)
        {
            _listArrivals = new ReloadOnArrival(null, () => MessengerThreadViewModel.OnUi(() => LoadListAsync(quiet: true)));
        }
    }

    /// <summary>Signed out: the opened keys are forgotten and the tab goes.</summary>
    public void Reset()
    {
        Forget();
        IsOffered = false;
    }

    private void Forget()
    {
        _session?.Lock();
        _listArrivals?.Stop();
        (_session, _boundTo, _listArrivals, _list, _preferences, TypingIndicator) = (null, null, null, null, null, null);
        Selected = null;
        CloseRightPane();
        Conversations.Clear();
    }

    /// <summary>
    /// The Messenger session, only while it was built over the window's CURRENT client. After a sign-in that did not
    /// rediscover yet, there is none: a client of an ended session sends no bearer, and its 401 ended the new
    /// session's slot too (2026-10-23).
    /// </summary>
    private ChatSession? Current => ReferenceEquals(_boundTo, _api()) ? _session : null;

    [RelayCommand]
    public async Task LoadAsync()
    {
        await LoadListAsync(quiet: false);
        if (Current is not null && Thread is { IsClosed: false } thread && Pane == MessagesPane.Thread)
        {
            await thread.RefreshAsync();   // Refresh reads the conversation on show too, as the phone's pull does
        }
    }

    private async Task LoadListAsync(bool quiet)
    {
        if (_session is not null && Current is null)
        {
            await DiscoverAsync();   // the session changed under the tab: rediscover over the current one
        }

        if (Current is not { } session)
        {
            return;
        }

        var keep = Selected?.Conversation.Id;
        ConversationList list;
        try
        {
            list = await session.Api.ConversationsAsync();
        }
        catch (Exception e) when (Refusal(e) is { } status)
        {
            // Not a lost connection, and the client has already renewed and replayed once: the server answers and
            // refuses THIS endpoint for the current session. The reconnect modal would not fix it and would come back
            // on every click, so say it here and in the log (ADR 0626).
            DesktopLog.Warn(e, "Messages: the Messenger refused the conversation list with {Status} for the current session; "
                + "the tab stays empty (the client's Trace log carries the exchange)", status);
            if (!quiet)
            {
                _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgLoadFailed"), e.Message));
            }

            return;
        }
        catch (Exception e) when (quiet && MessengerRefusal.IsReported(e))
        {
            return;   // nobody asked for this read: the next arrival or Refresh tries again
        }

        if (!ReferenceEquals(session, Current))
        {
            return;   // the session changed while the list was on its way
        }

        _list = list;
        Conversations.Clear();
        foreach (var conversation in list.Items)
        {
            Conversations.Add(new MessagesConversationViewModel { Conversation = conversation });
        }

        EmptyMessage = Conversations.Count == 0 ? ViewText.Get("NoConversations") : string.Empty;
        // The conversation's FRESH row, or none once the user is out of it: never the row from before (ADR 0661).
        Selected = MessagesSplit.AfterReload(keep, list.Items) is { } fresh ? Conversations.First(c => c.Conversation.Id == fresh.Id) : null;
        await LoadPreferencesAsync(session, list);
    }

    // A refusal of an authenticated request (401/403) as opposed to a failure to reach the server: the status, or null.
    internal static int? Refusal(Exception e) => e switch
    {
        MessengerApiException { StatusCode: 401 or 403 } refused => refused.StatusCode,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } refused => (int)refused.StatusCode!.Value,
        _ => null,
    };

    // The selection is the tab's (ADR 0661): a new conversation gets a new thread, and the old one is let go of — nothing
    // it was still reading lands here. The same conversation, re-listed, keeps its thread.
    partial void OnSelectedChanged(MessagesConversationViewModel? value)
    {
        if (value is not null && Thread is { IsClosed: false } shown && shown.Conversation.Id == value.Conversation.Id)
        {
            return;
        }

        CloseRightPane();
        if (value is null || Current is not { } session)
        {
            return;
        }

        var thread = new MessengerThreadViewModel(session, value.Conversation, _live, _shell, Dialogs, new MessengerThreadHost(
            ShowFile, ShowConversation, ShowMembers, Left, () => _listArrivals?.Arrived(null)));
        if (!_inFront)
        {
            thread.Paused();
        }

        (Thread, Pane) = (thread, MessagesPane.Thread);
        Safe.Fire(thread.OpenAsync);
    }

    private void CloseRightPane()
    {
        Thread?.Close();
        File?.Discard();
        (Thread, File, People, Members, Pane) = (null, null, null, null, MessagesPane.None);
    }

    private void ShowFile(MessengerFileViewModel? file)
    {
        File?.Discard();
        File = file;
        Pane = file is not null ? MessagesPane.File : Thread is not null ? MessagesPane.Thread : MessagesPane.None;
    }

    // A conversation made or continued here: listed and selected.
    private void ShowConversation(Conversation conversation) => Safe.Fire(() => SelectAsync(conversation.Id));

    private async Task SelectAsync(Guid conversation)
    {
        Selected = null;
        await LoadListAsync(quiet: false);
        Selected = Conversations.FirstOrDefault(c => c.Conversation.Id == conversation);
    }

    private void ShowMembers(Conversation group)
    {
        if (Current is not { } session)
        {
            return;
        }

        var members = new MessengerMembersViewModel(session, group, Dialogs, AddMember, Left, BackToThread, Failed);
        (Members, Pane) = (members, MessagesPane.Members);
        Safe.Fire(members.LoadAsync);
    }

    private void AddMember(Conversation group) => OpenPeople(MessengerPeopleMode.AddMember, group);

    // Out of the group: its thread goes, and the list says what is left.
    private void Left() => Safe.Fire(async () =>
    {
        Selected = null;
        await LoadListAsync(quiet: false);
    });

    private void BackToThread()
    {
        (People, Members) = (null, null);
        Pane = Thread is not null ? MessagesPane.Thread : MessagesPane.None;
    }

    private void Failed(Exception e)
    {
        DesktopLog.Warn(e, "Messages: an act failed");
        _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgActFailed"), MessengerRefusal.Wording(e)));
    }

    /// <summary>New chat: the people picker, one person.</summary>
    [RelayCommand]
    private void NewChat() => OpenPeople(MessengerPeopleMode.Chat, group: null);

    /// <summary>New group: a name and several people.</summary>
    [RelayCommand]
    private void NewGroup() => OpenPeople(MessengerPeopleMode.Group, group: null);

    private void OpenPeople(MessengerPeopleMode mode, Conversation? group)
    {
        if (Current is not { } session || (_list is null && group is null))
        {
            return;
        }

        var people = new MessengerPeopleViewModel(session, mode, _list, group, Dialogs, made =>
        {
            if (made is not null)
            {
                ShowConversation(made);
            }
            else if (mode == MessengerPeopleMode.AddMember && group is not null)
            {
                ShowMembers(group);   // back to the members, read again with the newcomer
            }
            else
            {
                BackToThread();
            }
        }, Failed);
        (People, Pane) = (people, MessagesPane.People);
        Safe.Fire(people.LoadAsync);
    }

    /// <summary>Esc: an opened file, the people picker or the members go first, then a reply or an edit in the composer.</summary>
    public bool Escape()
    {
        switch (Pane)
        {
            case MessagesPane.File:
                ShowFile(null);
                return true;
            case MessagesPane.People or MessagesPane.Members:
                BackToThread();
                return true;
            case MessagesPane.Thread when Thread?.Composer is { HasBar: true } composer:
                composer.CancelBar();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Something new arrived for the user — a "new messages" signal off the live hub, in <paramref name="conversation"/>
    /// where it says (null: somewhere it cannot say): the list reads again, and the thread on show when it is that
    /// conversation's — as a push reloads the phone's open screen (Mobile ADR 0026).
    /// </summary>
    public void Arrived(Guid? conversation)
    {
        _listArrivals?.Arrived(conversation);
        Thread?.Arrived(conversation);
    }

    /// <summary>The window left the front, or came back: arrivals wait meanwhile, so no key prompt comes up unseen.</summary>
    public void WindowActive(bool active)
    {
        _inFront = active;
        if (active)
        {
            _listArrivals?.Resumed();
            Thread?.Resumed();
        }
        else
        {
            _listArrivals?.Paused();
            Thread?.Paused();
        }
    }

    /// <summary>
    /// The key left the machine (the card was pulled): the conversation keys are forgotten — not only at sign-out — and
    /// what was decrypted with them goes from the screen, as the phone's sealed-content guard does (ADR 0967).
    /// </summary>
    public async Task KeyLeftAsync()
    {
        _session?.Lock();
        File?.Discard();
        if (Thread is { } thread)
        {
            await thread.KeyLeftAsync();
        }
    }

    /// <summary>
    /// Messages a person from elsewhere in the window — the details pane's "Created by" row (ADR 0974). Finds or
    /// creates the one-to-one conversation and shows it; false when the Messenger is not offered here or the person
    /// is not among its people (no enrolled key, so nothing could be sealed to them).
    /// </summary>
    public async Task<bool> MessageAsync(Guid personId, string displayName)
    {
        if (!IsOffered)
        {
            await DiscoverAsync();   // the Messages tab may never have been opened: discovery is the tab's, run here
        }

        if (Current is not { } session)
        {
            return false;
        }

        try
        {
            var list = _list ?? await session.Api.ConversationsAsync();
            var person = (await session.Api.PeopleAsync(displayName)).Items.FirstOrDefault(p => p.Id == personId);
            if (person is null)
            {
                return false;   // not offered by the people search: no enrolled key (the module's rule)
            }

            ShowConversation(await session.StartDirectAsync(list, person));
            _list ??= list;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failed(e);
            return false;
        }
    }

    /// <summary>
    /// Whether the Messenger could reach <paramref name="personId"/>: they are among the people its search offers —
    /// i.e. they hold an enrolled key. The details pane's action is disabled with a tooltip when this is false
    /// (ADR 0974); a lookup, never a guess from a name.
    /// </summary>
    public async Task<bool> HasKeyAsync(Guid personId, string displayName)
    {
        if (Current is not { } session)
        {
            return false;
        }

        try
        {
            return (await session.Api.PeopleAsync(displayName)).Items.Any(p => p.Id == personId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DesktopLog.Debug("Messages: the people search failed ({Reason}); the person is treated as unreachable", e.Message);
            return false;
        }
    }

    /// <summary>
    /// A "new messages" notification was clicked: its conversation opens here. False when it is none of the user's
    /// conversations (another module's signal, or one they left since), so the bell does what it does for any other entry.
    /// </summary>
    public async Task<bool> OpenConversationAsync(Guid conversation)
    {
        if (!IsOffered)
        {
            return false;
        }

        await LoadListAsync(quiet: false);
        if (Conversations.FirstOrDefault(c => c.Conversation.Id == conversation) is not { } row)
        {
            return false;
        }

        Selected = row;
        return true;
    }

    private async Task LoadPreferencesAsync(ChatSession session, ConversationList list)
    {
        if (!list.Offers("preferences"))
        {
            (_preferences, TypingIndicator) = (null, null);
            return;
        }

        try
        {
            _preferences = await session.Api.PreferencesAsync(list);
            SetTypingQuietly(_preferences.TypingIndicator);
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            DesktopLog.Debug("Messages: the preferences could not be read: {Reason}", e.Message);
        }
    }

    private bool _settingTyping;

    private void SetTypingQuietly(bool value)
    {
        _settingTyping = true;
        TypingIndicator = value;
        _settingTyping = false;
    }

    // The ribbon's toggle: replaced provided nobody changed it elsewhere since it was read; it works both ways, so the
    // thread then neither sends nor shows typing (the server stops offering it).
    partial void OnTypingIndicatorChanged(bool? oldValue, bool? newValue)
    {
        if (_settingTyping || newValue is not { } on || _preferences is not { } read || Current is not { } session)
        {
            return;
        }

        Safe.Fire(async () =>
        {
            try
            {
                _preferences = await session.Api.ReplacePreferencesAsync(read, on);
                _shell.Report(ViewText.Get("PreferencesChanged"));
            }
            catch (Exception e) when (MessengerRefusal.IsReported(e))
            {
                Failed(e);
                if (_list is not null)
                {
                    await LoadPreferencesAsync(session, _list);   // shown as it is now
                }
            }

            if (Thread is { } thread)
            {
                await thread.RefreshAsync();   // the conversation offers typing, or no longer
            }
        });
    }

    /// <summary>Writes the user's conversations to a file they choose: sealed, nothing decrypted, no key asked for.</summary>
    [RelayCommand]
    public async Task BackUpAsync()
    {
        if (Current is not { } session || SaveFile is null || _api() is not { } api
            || await SaveFile($"messages-{DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}{SimplArchive.Presentation.WireNames.MessengerBackupExtension}") is not { } file)
        {
            return;
        }

        await Reported("MobBackupFailed", async () =>
        {
            await using (file)
            {
                var summary = await MessengerBackup.WriteAsync(session.Api, file, api.Core.Http.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? string.Empty);
                _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MobBackupDone"), summary.Conversations, summary.Messages, summary.Files));
            }
        });
    }

    /// <summary>Restores a backup here: each conversation as history, which continues here once everyone is here.</summary>
    [RelayCommand]
    public async Task RestoreAsync()
    {
        if (Current is not { } session || OpenFile is null || await OpenFile() is not { } file)
        {
            return;
        }

        await Reported("MsgRestoreFailed", async () =>
        {
            // Read whole first: a backup is checked entry by entry before anything is restored, which needs to seek.
            using var copy = new MemoryStream();
            await using (file)
            {
                await file.CopyToAsync(copy);
            }

            copy.Position = 0;
            var report = await MessengerBackup.RestoreAsync(session.Api, copy);
            var done = string.Format(CultureInfo.CurrentCulture, Strings.Get("MobBackupRestored"), report.Conversations, report.Imported, report.Skipped);
            _shell.Report(report.StillHere == 0 ? done
                : $"{done} {string.Format(CultureInfo.CurrentCulture, Strings.Get("MobBackupStillHere"), report.StillHere)}");
            await LoadAsync();
        });
    }

    // A refusal or a failure in the status line, in words a person can act on, worded for the act that failed
    // (an attachment that would not open once read "The backup failed", #1878), and logged: the status line is gone
    // on the next report, and nobody would otherwise find out why (ADR 0626).
    internal async Task Reported(string failedKey, Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (e is BackupDamagedException || MessengerRefusal.IsReported(e))
        {
            DesktopLog.Warn(e, "Messages: {Act} failed", failedKey);
            _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get(failedKey), e.Message));
        }
    }
}
