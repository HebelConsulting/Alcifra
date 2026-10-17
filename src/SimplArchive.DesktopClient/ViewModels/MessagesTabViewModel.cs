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

/// <summary>A conversation in the list: its title, and whether it is restored history or has unread messages.</summary>
public sealed class MessagesConversationViewModel
{
    public required Conversation Conversation { get; init; }

    public string Title => ThreadRows.Title(Conversation);

    public string Detail => Conversation.Restored ? ViewText.Get("RestoredMark")
        : Conversation.Unread > 0 ? Conversation.Unread.ToString(CultureInfo.CurrentCulture) : string.Empty;
}

/// <summary>One bubble as the desktop draws it, from the row the phone draws too (the Messenger's display rules).</summary>
public sealed class MessageBubbleViewModel(ThreadRow row)
{
    public ThreadRow Row { get; } = row;

    public bool IsMine => Row.IsMine;

    public string? Sender => Row.SenderName;

    public bool HasSender => Row.SenderName is not null;

    public string Text => Row.Text;

    public bool HasText => Row.File is null || Row.Text.Length > 0;

    public bool IsPlaceholder => Row.IsPlaceholder;

    public string? Quote => Row.Quote;

    public bool HasQuote => Row.Quote is not null;

    public string? FileName => Row.File is { } file ? $"📎 {file.Name}" : null;

    public bool HasFile => Row.File is not null;

    public string Footer => Row.Footer;

    public string? Receipt => Row.Receipt;

    public bool HasReceipt => Row.Receipt is not null;
}

/// <summary>
/// The Messages tab (core #1813, owner decisions 2026-10-17): every conversation of the user, READ here — writing and
/// replying stay on the phone for now — with Back up and Restore in the ribbon. Shown only while the archive offers the
/// Messenger to this user. Threads are drawn with the Messenger's own display rules, so a bubble reads as on the phone.
/// </summary>
public sealed partial class MessagesTabViewModel : ObservableObject
{
    private readonly IShellContext _shell;
    private readonly Func<SimplArchiveApiClient?> _api;
    private ChatSession? _session;
    private Guid? _showing;

    /// <param name="api">The signed-in client, read when needed: the tab is built before sign-in.</param>
    public MessagesTabViewModel(IShellContext shell, Func<SimplArchiveApiClient?> api) => (_shell, _api) = (shell, api);

    public ObservableCollection<MessagesConversationViewModel> Conversations { get; } = [];

    public ObservableCollection<MessageBubbleViewModel> Bubbles { get; } = [];

    /// <summary>The archive offers the Messenger to this user: what shows the tab.</summary>
    [ObservableProperty] private bool _isOffered;

    [ObservableProperty] private MessagesConversationViewModel? _selected;

    [ObservableProperty] private string _emptyMessage = string.Empty;

    /// <summary>Beneath the thread: restored history says where it came from; a live one that replies are on the phone.</summary>
    [ObservableProperty] private string _threadNote = string.Empty;

    /// <summary>The view's save dialog: a stream for the suggested file name, or null when cancelled (ADR 0730: the view's).</summary>
    public Func<string, Task<Stream?>>? SaveFile { get; set; }

    /// <summary>The view's open dialog: the chosen backup, or null when cancelled.</summary>
    public Func<Task<Stream?>>? OpenFile { get; set; }

    /// <summary>Asks the archive whether it offers the Messenger here; the tab shows only if it does. Run at sign-in.</summary>
    public async Task DiscoverAsync()
    {
        Reset();
        if (_api() is not { } api)
        {
            return;
        }

        try
        {
            var messenger = new MessengerApi(api.Core.Http);
            if (!await messenger.DiscoverAsync() || await new WhoAmIClient(api.Core).GetAsync() is not { UserId: { } me } who)
            {
                return;
            }

            _session = new ChatSession(messenger, new ConversationKeyOpener(), me, who.UserName ?? string.Empty);
            IsOffered = true;
        }
        catch (Exception e) when (e is HttpRequestException or MessengerApiException or TaskCanceledException)
        {
            DesktopLog.Debug("The Messenger could not be discovered; no Messages tab: {Reason}", e.Message);
        }
    }

    /// <summary>Signed out: the opened keys are forgotten and the tab goes.</summary>
    public void Reset()
    {
        _session?.Lock();
        (_session, _showing, IsOffered, Selected) = (null, null, false, null);
        Conversations.Clear();
        Bubbles.Clear();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_session is null)
        {
            return;
        }

        var keep = Selected?.Conversation.Id;
        var list = await _session.Api.ConversationsAsync();
        Conversations.Clear();
        foreach (var conversation in list.Items)
        {
            Conversations.Add(new MessagesConversationViewModel { Conversation = conversation });
        }

        EmptyMessage = Conversations.Count == 0 ? ViewText.Get("NoConversations") : string.Empty;
        Selected = Conversations.FirstOrDefault(c => c.Conversation.Id == keep);
    }

    partial void OnSelectedChanged(MessagesConversationViewModel? value) => Safe.Fire(() => ShowAsync(value));

    // The thread of the selected conversation. A pane that has not caught up shows NOTHING, never the previous
    // conversation (ADR 0661); a slower answer for a conversation no longer selected is dropped.
    private async Task ShowAsync(MessagesConversationViewModel? item)
    {
        Bubbles.Clear();
        ThreadNote = string.Empty;
        _showing = item?.Conversation.Id;
        if (item is null || _session is not { } session)
        {
            return;
        }

        var conversation = await session.Api.RefreshAsync(item.Conversation);
        var rows = new List<ThreadRow>();
        if (conversation.Offers("earlier"))
        {
            foreach (var earlier in (await session.Api.EarlierAsync(conversation)).Items)
            {
                rows.AddRange(await RowsAsync(session, earlier));
            }
        }

        var (_, messages) = await session.MessagesAsync(conversation);
        var opened = new List<OpenedMessage>();
        foreach (var message in messages)
        {
            opened.Add(await session.OpenAsync(conversation, message));   // the first asks for the key, once
        }

        rows.AddRange(ThreadRows.Build(conversation, opened, session.Me));
        if (_showing != conversation.Id)
        {
            return;
        }

        foreach (var row in rows)
        {
            Bubbles.Add(new MessageBubbleViewModel(row));
        }

        ThreadNote = conversation.IsReadOnly
            ? ViewText.Get("RestoredReadOnly", conversation.RestoredFrom ?? string.Empty)
            : Strings.Get("MsgReadOnlyHere");
        if (messages.Count > 0)
        {
            await session.Api.MarkReadAsync(conversation, messages[^1].SentAt);   // read here: the phone's signal clears
        }
    }

    private static async Task<IReadOnlyList<ThreadRow>> RowsAsync(ChatSession session, Conversation conversation)
    {
        var (_, messages) = await session.MessagesAsync(conversation);
        var opened = new List<OpenedMessage>();
        foreach (var message in messages)
        {
            opened.Add(await session.OpenAsync(conversation, message));
        }

        return ThreadRows.Build(conversation, opened, session.Me);
    }

    /// <summary>Opens a bubble's photo or file in its own application: decrypted with the reader's key, here, now.</summary>
    [RelayCommand]
    public async Task OpenFileAsync(MessageBubbleViewModel? bubble)
    {
        if (bubble?.Row.File is not { } file || _session is not { } session)
        {
            return;
        }

        await Reported(async () => await NativeFileOpener.OpenBytesAsync(await session.OpenFileAsync(bubble.Row.Message), file.Name));
    }

    /// <summary>Writes the user's conversations to a file they choose: sealed, nothing decrypted, no key asked for.</summary>
    [RelayCommand]
    public async Task BackUpAsync()
    {
        if (_session is not { } session || SaveFile is null || _api() is not { } api
            || await SaveFile($"messages-{DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.simplarchive-messages") is not { } file)
        {
            return;
        }

        await Reported(async () =>
        {
            await using (file)
            {
                var summary = await MessengerBackup.WriteAsync(session.Api, file, api.Core.Http.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? string.Empty);
                _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MobBackupDone"), summary.Conversations, summary.Messages, summary.Files));
            }
        });
    }

    /// <summary>Restores a backup here: each conversation as history, which continues on the phone once everyone is here.</summary>
    [RelayCommand]
    public async Task RestoreAsync()
    {
        if (_session is not { } session || OpenFile is null || await OpenFile() is not { } file)
        {
            return;
        }

        await Reported(async () =>
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

    // A refusal or a failure in the status line, in words a person can act on.
    private async Task Reported(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (e is HttpRequestException or MessengerApiException or BackupDamagedException or EnvelopeNotOpenedException
            or IOException or UnauthorizedAccessException)
        {
            _shell.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MobBackupFailed"), e.Message));
        }
    }

    /// <summary>
    /// Demo conversations for the headless <c>--screenshot --demo --messages</c> hook: a group with a reply, a photo, a
    /// receipt and a restored conversation — built through the same display rules as a real thread, with no server.
    /// </summary>
    public void PopulateDemoForScreenshot()
    {
        var (me, tom, ina) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var start = DateTimeOffset.Now.Date.AddHours(9);
        var crew = new Conversation
        {
            Id = Guid.NewGuid(),
            Name = "Flight crew",
            Members =
            [
                new Member { UserId = me, DisplayName = "Anna" },
                new Member { UserId = tom, DisplayName = "Tom", ReadUpTo = start.AddMinutes(30) },
                new Member { UserId = ina, DisplayName = "Ina" },
            ],
        };
        Message At(Guid sender, int minutes, Guid? replyTo = null) =>
            new() { Id = Guid.NewGuid(), SenderUserId = sender, SentAt = start.AddMinutes(minutes), ReplyToId = replyTo };
        var hello = new OpenedMessage(At(tom, 0), "Briefing moved to 10:30, hangar 2.", MessageState.Readable);
        var opened = new List<OpenedMessage>
        {
            hello,
            new(At(me, 4, hello.Message.Id), "Thanks, I'll bring the weather sheet.", MessageState.Readable),
            new(At(ina, 9), "Here is the route.", MessageState.Readable) { File = new SentFile("route-LSZH-EDDM.pdf", "application/pdf") },
            new(At(me, 12), "Looks good.", MessageState.Readable),
        };
        Conversations.Clear();
        Conversations.Add(new MessagesConversationViewModel { Conversation = crew });
        Conversations.Add(new MessagesConversationViewModel { Conversation = new Conversation { Name = "Tom", IsDirect = true, CounterpartName = "Tom", Unread = 2 } });
        Conversations.Add(new MessagesConversationViewModel { Conversation = new Conversation { Name = "Club (restored)", Restored = true, IsReadOnly = true } });
        Bubbles.Clear();
        foreach (var row in ThreadRows.Build(crew, opened, me))
        {
            Bubbles.Add(new MessageBubbleViewModel(row));
        }

        (IsOffered, ThreadNote) = (true, Strings.Get("MsgReadOnlyHere"));
    }
}
