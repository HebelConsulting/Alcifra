using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Messenger.Client;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.UiEndToEndTests;

// The desktop's Messages tab at parity with the phone's Messenger (ADR 0967): writing, replying, editing, deleting,
// files as chips, groups and their members, leaving, and the security rules — a key that leaves takes the conversation
// keys with it, a thread whose key did not open shows sealed and is not marked read, and a file opened in the app leaves
// nothing readable in the temp folder. Against an in-memory Messenger (FakeMessenger) with real sealing: every key is
// wrapped to a real certificate and every message sealed with the Messenger client's own format.
[Collection(UiCollection.Name)]
public sealed class DesktopMessagesParityTests : IDisposable
{
    private readonly string _apiBaseUrl = DesktopClientOptions.ApiBaseUrl;
    private readonly Func<X509Certificate2Collection> _keys = EnvelopeOpener.Keys;
    private readonly string? _temp = NativeFileOpener.TempDirectoryOverride;
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"messages-temp-{Guid.NewGuid():N}");
    private readonly X509Certificate2 _anna = NewCertificate("Anna");
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _tom = Guid.NewGuid();
    private readonly Guid _ina = Guid.NewGuid();
    private readonly FakeMessenger _messenger;
    private readonly TestShell _shell = new();

    public DesktopMessagesParityTests()
    {
        _messenger = new FakeMessenger(_me, "Anna", _anna);
        _messenger.AddPerson(_tom, "Tom", NewCertificate("Tom"));
        _messenger.AddPerson(_ina, "Ina", NewCertificate("Ina"));
        DesktopClientOptions.ApiBaseUrl = _messenger.BaseUrl;
        EnvelopeOpener.Keys = () => [_anna];   // Anna's key is on this machine, as a software key
        NativeFileOpener.TempDirectoryOverride = _tempDirectory;
    }

    public void Dispose()
    {
        _messenger.Dispose();
        DesktopClientOptions.ApiBaseUrl = _apiBaseUrl;
        EnvelopeOpener.Keys = _keys;
        NativeFileOpener.TempDirectoryOverride = _temp;
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_message_typed_here_is_sealed_sent_once_and_shown_in_the_thread()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var thread = await OpenAsync(id);

        thread.Composer.Text = "Briefing at 10:30";
        await thread.Composer.SendAsync();

        var sent = Assert.Single(_messenger.ChatOf(id).Messages);
        Assert.Equal("Briefing at 10:30", OpenText(id, key, sent));   // sealed with the conversation key, nothing else
        Assert.Equal(string.Empty, thread.Composer.Text);
        Assert.Contains(thread.Bubbles, b => b.IsMine && b.Text == "Briefing at 10:30");
    }

    [Fact]
    public async Task While_a_message_is_on_its_way_it_shows_as_sending_and_a_second_send_is_not_made()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var thread = await OpenAsync(id);
        _messenger.HoldSends = new TaskCompletionSource();

        thread.Composer.Text = "once";
        var first = thread.Composer.SendAsync();
        await Eventually(() => thread.Composer.IsSending);
        Assert.Contains(thread.Bubbles, b => b.IsSending && b.Footer == ViewText.Get("Sending"));
        await thread.Composer.SendAsync();   // a second click while the first is on its way
        _messenger.HoldSends.SetResult();
        await first;

        Assert.Single(_messenger.ChatOf(id).Messages);
        Assert.DoesNotContain(thread.Bubbles, b => b.IsSending);
    }

    [Fact]
    public async Task A_reply_carries_the_message_it_answers_and_Escape_cancels_it()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var original = _messenger.Seal(id, key, _tom, "Who brings the weather?");
        var tab = NewTab();
        var thread = await OpenAsync(id, tab);
        var bubble = thread.Bubbles.Single(b => b.Row.Message.Id == original);

        thread.ReplyCommand.Execute(bubble);
        Assert.Equal(ViewText.Get("ReplyingTo", "Tom"), thread.Composer.BarLine);
        Assert.True(tab.Escape());   // Esc cancels the reply
        Assert.False(thread.Composer.HasBar);

        thread.ReplyCommand.Execute(bubble);
        thread.Composer.Text = "I do";
        await thread.Composer.SendAsync();

        Assert.Equal(original, _messenger.ChatOf(id).Messages.Single(m => m.Sender == _me).ReplyTo);
        Assert.Contains(thread.Bubbles, b => b.IsMine && b.Quote == "Tom: Who brings the weather?");
    }

    [Fact]
    public async Task An_own_message_is_edited_in_the_composer_and_saved_with_the_tag_it_was_read_with()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var thread = await OpenAsync(id);
        thread.Composer.Text = "10:30";
        await thread.Composer.SendAsync();
        var mine = thread.Bubbles.Single(b => b.IsMine);
        var tag = _messenger.ChatOf(id).Messages.Single().ETag;

        thread.EditCommand.Execute(mine);
        Assert.True(thread.Composer.IsEditing);
        Assert.Equal("10:30", thread.Composer.Text);
        thread.Composer.Text = "11:00";
        await thread.Composer.SendAsync();

        var stored = _messenger.ChatOf(id).Messages.Single();
        Assert.Equal("11:00", OpenText(id, key, stored));
        Assert.NotEqual(tag, stored.ETag);
        Assert.False(thread.Composer.IsEditing);
        Assert.Contains(thread.Bubbles, b => b.IsMine && b.Text == "11:00" && b.Footer.Contains(ViewText.Get("Edited"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_for_everyone_leaves_its_place_and_deleting_for_me_hides_it()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var theirs = _messenger.Seal(id, key, _tom, "spam");
        var thread = await OpenAsync(id);
        thread.Composer.Text = "typo";
        await thread.Composer.SendAsync();

        await thread.DeleteForEveryoneCommand.ExecuteAsync(thread.Bubbles.Single(b => b.IsMine));
        await thread.DeleteForMeCommand.ExecuteAsync(thread.Bubbles.Single(b => b.Row.Message.Id == theirs));

        Assert.NotNull(_messenger.ChatOf(id).Messages.Single(m => m.Sender == _me).DeletedAt);
        Assert.Contains(theirs, _messenger.ChatOf(id).Hidden);
        var left = Assert.Single(thread.Bubbles);
        Assert.Equal(ViewText.Get("Deleted"), left.Text);
    }

    [Fact]
    public async Task Several_files_go_as_chips_into_one_message_each_sealed_to_every_member()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var thread = await OpenAsync(id);
        Assert.Null(thread.Composer.Add(new PickedFile("route.pdf", Encoding.UTF8.GetBytes("%PDF-1.4 route"))));
        Assert.Null(thread.Composer.Add(new PickedFile("notes.txt", Encoding.UTF8.GetBytes("fuel 42"))));
        thread.Composer.RemoveChipCommand.Execute(thread.Composer.Chips[0]);   // ✕ takes one off again
        Assert.Null(thread.Composer.Add(new PickedFile("route.pdf", Encoding.UTF8.GetBytes("%PDF-1.4 route"))));
        Assert.Equal(2, thread.Composer.Chips.Count);

        thread.Composer.Text = "for tomorrow";
        await thread.Composer.SendAsync();

        var sent = Assert.Single(_messenger.ChatOf(id).Messages);
        Assert.Equal(2, sent.Files.Count);
        Assert.Equal("fuel 42", Encoding.UTF8.GetString(OpenEnvelope(_messenger.Document(sent.Files[0]))));   // the card's envelope, Anna's key
        Assert.Empty(thread.Composer.Chips);
        var bubble = thread.Bubbles.Single(b => b.IsMine);
        Assert.Equal(["📎 notes.txt", "📎 route.pdf"], bubble.Files.Select(f => f.Label));
        Assert.Equal("for tomorrow", bubble.Text);
    }

    [Fact]
    public async Task A_sent_text_file_opens_in_the_app_from_memory_and_nothing_readable_reaches_the_temp_folder()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        var thread = await OpenAsync(id, tab);
        const string secret = "the hangar code is 4711";
        thread.Composer.Add(new PickedFile("code.txt", Encoding.UTF8.GetBytes(secret)));
        await thread.Composer.SendAsync();

        await thread.OpenFileCommand.ExecuteAsync(thread.Bubbles.Single(b => b.IsMine).Files[0]);

        Assert.Equal(MessagesPane.File, tab.Pane);
        Assert.True(tab.File!.Shown);
        Assert.Equal(secret, tab.File.Preview.PreviewText);
        AssertTempHoldsNo(secret);
        Assert.True(tab.Escape());   // Esc goes back to the conversation and lets go of the bytes
        Assert.Equal(MessagesPane.Thread, tab.Pane);
    }

    [Fact]
    public async Task Where_files_stay_in_the_app_a_file_it_cannot_show_is_never_handed_on()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom], filesStayInApp: true);
        var tab = NewTab();
        tab.Dialogs.Confirm = (_, _, _) => Task.FromResult(true);   // whatever the warning, it would be confirmed
        var thread = await OpenAsync(id, tab);
        const string secret = "PK-office-document-body";
        thread.Composer.Add(new PickedFile("plan.docx", Encoding.UTF8.GetBytes(secret)));
        await thread.Composer.SendAsync();

        await thread.OpenFileCommand.ExecuteAsync(thread.Bubbles.Single(b => b.IsMine).Files[0]);
        Assert.False(tab.File!.MayLeaveTheApp);
        Assert.False(tab.File.Shown);
        Assert.Equal(ViewText.Get("KeptInApp"), tab.File.NotShownLine);
        await tab.File.OpenWithCommand.ExecuteAsync(null);

        AssertTempHoldsNo(secret);
    }

    [Fact]
    public async Task Elsewhere_a_file_the_app_cannot_show_is_handed_on_only_after_the_warning_is_confirmed()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        var warned = 0;
        tab.Dialogs.Confirm = (title, _, _) =>
        {
            warned++;
            Assert.Equal(ViewText.Get("HandOnTitle"), title);
            return Task.FromResult(false);   // declined
        };
        var thread = await OpenAsync(id, tab);
        const string secret = "PK-office-document-body";
        thread.Composer.Add(new PickedFile("plan.docx", Encoding.UTF8.GetBytes(secret)));
        await thread.Composer.SendAsync();

        await thread.OpenFileCommand.ExecuteAsync(thread.Bubbles.Single(b => b.IsMine).Files[0]);
        Assert.True(tab.File!.MayLeaveTheApp);
        await tab.File.OpenWithCommand.ExecuteAsync(null);

        Assert.Equal(1, warned);
        AssertTempHoldsNo(secret);   // declined: nothing written
    }

    [Fact]
    public async Task When_the_key_leaves_the_conversation_keys_are_forgotten_and_the_thread_shows_sealed()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        _messenger.Seal(id, key, _tom, "the secret plan");
        var tab = NewTab();
        var thread = await OpenAsync(id, tab);
        Assert.Contains(thread.Bubbles, b => b.Text == "the secret plan");

        await tab.KeyLeftAsync();

        Assert.False(thread.Session.Keys.Holds(id, 1));
        Assert.All(thread.Bubbles, b => Assert.True(b.IsLocked));
        Assert.DoesNotContain(thread.Bubbles, b => b.Row.Text.Contains("secret", StringComparison.Ordinal));
        Assert.True(thread.HasSealed);

        await thread.AskForKeyCommand.ExecuteAsync(null);   // the user's own click asks once more
        Assert.Contains(thread.Bubbles, b => b.Text == "the secret plan");
    }

    [Fact]
    public async Task A_key_that_is_not_here_shows_the_thread_sealed_never_empty_and_does_not_mark_it_read()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        _messenger.Seal(id, key, _tom, "one");
        _messenger.Seal(id, key, _tom, "two");
        EnvelopeOpener.Keys = () => [NewCertificate("Somebody else")];

        var thread = await OpenAsync(id, expect: 2);

        Assert.Equal(2, thread.Bubbles.Count);
        Assert.All(thread.Bubbles, b => Assert.True(b.IsLocked));
        Assert.True(thread.HasSealed);
        Assert.Equal(0, _messenger.RequestCount("POST", "/receipt"));   // sealed: not read, the phone's signal stays
        Assert.Contains(_shell.Reports, r => r.StartsWith(SimplArchive.Localization.Strings.Get("MsgKeyMissing").Replace("{0}", string.Empty), StringComparison.Ordinal));

        EnvelopeOpener.Keys = () => [_anna];
        await thread.AskForKeyCommand.ExecuteAsync(null);
        Assert.Equal(["one", "two"], thread.Bubbles.Select(b => b.Text));
        Assert.Equal(1, _messenger.RequestCount("POST", "/receipt"));
    }

    [Fact]
    public async Task A_new_group_is_created_with_a_key_for_everyone_and_selected()
    {
        _messenger.AddConversation("Existing", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        await tab.DiscoverAsync();
        await tab.LoadAsync();

        tab.NewGroupCommand.Execute(null);
        var people = tab.People!;
        await Eventually(() => people.People.Count == 2);   // Tom and Ina, never Anna herself
        people.GroupName = "Hangar 2";
        await people.ChooseCommand.ExecuteAsync(people.People.Single(p => p.Name == "Tom"));
        await people.ChooseCommand.ExecuteAsync(people.People.Single(p => p.Name == "Ina"));
        await people.CreateGroupCommand.ExecuteAsync(null);

        var group = _messenger.Chats.Single(c => c.Name == "Hangar 2");
        Assert.Equal(new[] { _me, _tom, _ina }.Order(), group.Members.Select(m => m.User).Order());
        Assert.Equal(3, group.Keys[1].Count);
        await Eventually(() => tab.Selected?.Conversation.Id == group.Id && tab.Pane == MessagesPane.Thread);
        Assert.Equal(ViewText.Get("GroupMembers", 3), tab.Selected!.Kind);
    }

    [Fact]
    public async Task A_new_chat_with_somebody_is_made_with_a_key_for_both_and_selected()
    {
        var tab = NewTab();
        await tab.DiscoverAsync();
        await tab.LoadAsync();

        tab.NewChatCommand.Execute(null);
        var people = tab.People!;
        await people.SearchAsync("to");   // as typed: Tom only
        await people.ChooseCommand.ExecuteAsync(Assert.Single(people.People));

        var chat = Assert.Single(_messenger.Chats);
        Assert.True(chat.IsDirect);
        Assert.Equal(new[] { _me, _tom }.Order(), chat.Keys[1].Keys.Order());
        await Eventually(() => tab.Selected?.Conversation.Id == chat.Id && tab.Thread?.Title == "Tom");
    }

    [Fact]
    public async Task An_administrator_adds_and_removes_members_each_time_with_a_new_key()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        tab.Dialogs.Confirm = (_, _, _) => Task.FromResult(true);
        var thread = await OpenAsync(id, tab);

        thread.ShowMembersCommand.Execute(null);
        var members = tab.Members!;
        await Eventually(() => members.Members.Count == 2);
        Assert.True(members.Members[0].IsAdmin);   // administrators first: Anna
        members.AddMemberCommand.Execute(null);
        var people = tab.People!;
        await Eventually(() => people.People.Count == 1);   // only Ina: Tom is in already
        await people.ChooseCommand.ExecuteAsync(people.People.Single());

        Assert.Equal(2, _messenger.ChatOf(id).Epoch);   // a new key, the newcomer's included
        Assert.Contains(_ina, _messenger.ChatOf(id).Keys[2].Keys);
        await Eventually(() => tab.Pane == MessagesPane.Members && tab.Members!.Members.Count == 3);

        await tab.Members!.RemoveCommand.ExecuteAsync(tab.Members.Members.Single(m => m.Name == "Tom"));
        Assert.Equal(3, _messenger.ChatOf(id).Epoch);
        Assert.Equal(new[] { _me, _ina }.Order(), _messenger.ChatOf(id).Keys[3].Keys.Order());
    }

    [Fact]
    public async Task The_last_administrator_is_told_why_they_cannot_step_down()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        var thread = await OpenAsync(id, tab);
        thread.ShowMembersCommand.Execute(null);
        await Eventually(() => tab.Members!.Members.Count == 2);

        await tab.Members!.RevokeAdminCommand.ExecuteAsync(tab.Members.Members.Single(m => m.Row.IsMe));

        Assert.Contains(_shell.Reports, r => r.Contains(ViewText.Get("LastAdmin"), StringComparison.Ordinal));
        Assert.True(_messenger.ChatOf(id).Members.Single(m => m.User == _me).IsAdmin);
    }

    [Fact]
    public async Task Leaving_is_confirmed_and_takes_the_conversation_out_of_the_list()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom, _ina], admin: _tom);
        var tab = NewTab();
        var asked = 0;
        tab.Dialogs.Confirm = (_, message, _) =>
        {
            asked++;
            Assert.Equal(ViewText.Get("LeaveConfirm"), message);
            return Task.FromResult(true);
        };
        var thread = await OpenAsync(id, tab);

        await thread.LeaveCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.DoesNotContain(_messenger.ChatOf(id).Members, m => m.User == _me);
        Assert.True(_messenger.ChatOf(id).NeedsNewEpoch);   // the one leaving makes no key: those who stay do
        await Eventually(() => tab.Selected is null && tab.Conversations.Count == 0);
    }

    [Fact]
    public async Task After_somebody_left_the_next_send_publishes_the_new_key_first()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom, _ina]);
        _messenger.ChatOf(id).NeedsNewEpoch = true;   // somebody left
        var thread = await OpenAsync(id);

        thread.Composer.Text = "still here";
        await thread.Composer.SendAsync();

        Assert.Equal([2], _messenger.ChatOf(id).Published);
        Assert.Equal(2, _messenger.ChatOf(id).Messages.Single().Epoch);
    }

    [Fact]
    public async Task Typing_is_told_to_the_others_where_the_conversation_offers_it()
    {
        _messenger.OffersTyping = true;
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var thread = await OpenAsync(id);

        thread.Composer.Text = "h";

        await Eventually(() => _messenger.RequestCount("POST", "/typing") >= 1);
    }

    [Fact]
    public async Task Something_arriving_in_the_conversation_on_show_reads_it_again()
    {
        var (id, key) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        var thread = await OpenAsync(id, tab);
        _messenger.Seal(id, key, _tom, "just arrived");

        tab.Arrived(id);

        await Eventually(() => thread.Bubbles.Any(b => b.Text == "just arrived"));
    }

    [Fact]
    public async Task A_new_messages_notification_opens_its_conversation_and_one_of_another_module_does_not()
    {
        var (id, _) = _messenger.AddConversation("Crew", isDirect: false, [_me, _tom]);
        var tab = NewTab();
        await tab.DiscoverAsync();

        Assert.True(await tab.OpenConversationAsync(id));
        Assert.Equal(id, tab.Selected?.Conversation.Id);
        Assert.False(await tab.OpenConversationAsync(Guid.NewGuid()));
    }

    [Fact]
    public void Only_unread_module_signals_name_conversations_to_read_again()
    {
        var conversation = Guid.NewGuid();
        NotificationRowViewModel Row(string type, Guid? document, bool read) => new(new SimplArchive.ApiClient.NotificationsClient.NotificationInfo(
            Guid.NewGuid(), type, "New messages", "Messenger", document, null, DateTimeOffset.UtcNow, read));

        var named = MainWindowViewModel.SignalledConversations(
            [Row("ModuleSignal", conversation, false), Row("ModuleSignal", conversation, false), Row("ModuleSignal", Guid.NewGuid(), true), Row("TaskAssigned", Guid.NewGuid(), false)]);

        Assert.Equal([conversation], named);
    }

    private MessagesTabViewModel NewTab()
    {
        var api = new SimplArchiveApiClient("token");
        return new MessagesTabViewModel(_shell, () => api, NoLiveEvents.Instance, () => "anna@example.com");
    }

    // The tab discovered, listed and showing the conversation, its thread read.
    private async Task<MessengerThreadViewModel> OpenAsync(Guid conversation, MessagesTabViewModel? tab = null, int expect = -1)
    {
        tab ??= NewTab();
        await tab.DiscoverAsync();
        await tab.LoadAsync();
        tab.Selected = tab.Conversations.Single(c => c.Conversation.Id == conversation);
        var thread = tab.Thread!;
        var messages = expect >= 0 ? expect : _messenger.ChatOf(conversation).Messages.Count;
        await Eventually(() => thread.Page is not null && thread.Bubbles.Count == messages);
        return thread;
    }

    private string OpenText(Guid conversation, byte[] key, FakeMessenger.Post post) =>
        Encoding.UTF8.GetString(MessageFormat.Open(key, new MessageContext(conversation, post.Epoch, post.Id, post.Sender), post.Ciphertext));

    private byte[] OpenEnvelope(byte[] cms)
    {
        var envelope = new EnvelopedCms();
        envelope.Decode(cms);
        envelope.Decrypt([_anna]);
        return envelope.ContentInfo.Content;
    }

    // Nothing in the temp folder holds the content in a readable form (neither the client's own nor the system's).
    private void AssertTempHoldsNo(string content)
    {
        foreach (var directory in new[] { _tempDirectory, Path.Combine(Path.GetTempPath(), "SimplArchive") }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(content, File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var waited = 0; waited < 15_000 && !condition(); waited += 25)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "The condition did not become true within 15 s.");
    }

    private static X509Certificate2 NewCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
