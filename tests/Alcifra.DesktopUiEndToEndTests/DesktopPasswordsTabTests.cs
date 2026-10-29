using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit.Cryptography;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;
using Alcifra.Presentation;
using Alcifra.UnitTests.PasswordStore;

namespace Alcifra.UiEndToEndTests;

// The desktop's Passwords tab at parity with the phone's Password Store (ADR 0968): an entry opened with the key shows in
// the preview in the core's field order, a secret only while its eye is held (the window kept out of capture meanwhile),
// a copied secret cleared after 30 s, find never matching a secret's value, a save under the ETag the entry was opened
// with (a 412 said as such), the one-PIN re-seal's report, the import asking only about clashing names, the key leaving
// closing what it opened, and nothing of an entry written to disk. Against an in-memory store (FakePasswordStore) with
// real sealing; the hardware key is a key held in memory, put where the card opener would be.
[Collection(UiCollection.Name)]
public sealed class DesktopPasswordsTabTests : IAsyncDisposable
{
    private static readonly PasswordEntryContent CodeHosting = new("c0rrect-H0rse!",
    [
        new PasswordEntryField(PasswordEntryFieldNames.Notes, "two lines\nof notes"),
        new PasswordEntryField("PIN", "482913", IsSecret: true),
        new PasswordEntryField(PasswordEntryFieldNames.UserName, "anna.berger"),
        new PasswordEntryField(PasswordEntryFieldNames.Url, "https://code.example/login"),
    ]);

    private readonly string _apiBaseUrl = DesktopClientOptions.ApiBaseUrl;
    private readonly IReadOnlyList<EnvelopeOpener.Opener> _openers = PasswordKeyOpener.Openers;
    private readonly Func<bool> _declined = PasswordKeyOpener.Declined;
    private readonly Func<IReadOnlyList<X509Certificate2>> _present = PasswordKeyOpener.Present;
    private readonly Func<CancellationToken, Task<EnrolledKeysAnswer>> _signedIn = EnvelopeOpener.SignedIn;
    private readonly X509Certificate2 _anna = NewCertificate("Anna");
    private readonly FakePasswordStore _store;
    private readonly TestShell _shell = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly ManualTime _time = new();
    private readonly List<bool> _protection = [];
    private readonly SecretScreens _screens;
    private string? _plainCopy;
    private bool _keyPresent = true;
    private bool _promptCancelled;
    private int _keyReads;
    private int _decrypts;

    public DesktopPasswordsTabTests()
    {
        _store = new FakePasswordStore(PublicOnly(_anna));
        _screens = new SecretScreens(_protection.Add);
        DesktopClientOptions.ApiBaseUrl = _store.BaseUrl;
        PasswordKeyOpener.Openers = [OpenWithAnnasKey];
        PasswordKeyOpener.Declined = () => _promptCancelled;
        PasswordKeyOpener.Present = () => _keyPresent ? [_anna] : [];
        SignedInAs(_anna);   // Anna is signed in, and the key is hers (ADR 0969)
    }

    private void SignedInAs(X509Certificate2 enrolled) =>
        EnvelopeOpener.SignedIn = _ => Task.FromResult(new EnrolledKeysAnswer([EnrolledKey.Of(enrolled, "Key")]));

    public async ValueTask DisposeAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _apiBaseUrl;
        PasswordKeyOpener.Openers = _openers;
        PasswordKeyOpener.Declined = _declined;
        PasswordKeyOpener.Present = _present;
        EnvelopeOpener.SignedIn = _signedIn;
        await _store.DisposeAsync();
    }

    [Fact]
    public async Task An_entry_opened_with_the_key_shows_in_the_preview_in_the_cores_order_its_secrets_hidden()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var tab = await OpenAsync("Code hosting");

        var rows = tab.Preview.PasswordEntry!.Rows;
        Assert.Equal([Strings.Get("PwUrl"), Strings.Get("PwUserName"), Strings.Get("PwPassword"), Strings.Get("MobKeyPin"), Strings.Get("PwNotes")], rows.Select(r => r.Label));
        Assert.Equal(["https://code.example/login", "anna.berger", SecretFields.Mask, SecretFields.Mask, "two lines\nof notes"], rows.Select(r => r.Display));
        Assert.True(tab.Preview.ContentWasEnveloped);   // what the key opened: the key leaving discards it
        Assert.Equal(1, _keyReads);
    }

    [Fact]
    public async Task A_secret_shows_only_while_its_eye_is_held_and_the_window_is_kept_out_of_capture_meanwhile()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var password = (await OpenAsync("Code hosting")).Preview.PasswordEntry!.Rows.Single(r => r.Label == Strings.Get("PwPassword"));

        password.Press();
        Assert.Equal("c0rrect-H0rse!", password.Display);
        Assert.Equal([true], _protection);
        password.Release();

        Assert.Equal(SecretFields.Mask, password.Display);
        Assert.Equal([true, false], _protection);
    }

    [Fact]
    public async Task A_copied_secret_is_marked_sensitive_and_cleared_after_thirty_seconds_and_a_user_name_is_copied_plainly()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var rows = (await OpenAsync("Code hosting")).Preview.PasswordEntry!.Rows;

        var clearing = rows.Single(r => r.Label == Strings.Get("PwPassword")).CopyForTestAsync();
        Assert.Equal("c0rrect-H0rse!", _clipboard.Text);
        Assert.True(_clipboard.Sensitive);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(clearing.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await clearing);
        Assert.Null(_clipboard.Text);

        await rows.Single(r => r.Label == Strings.Get("PwUserName")).CopyCommand.ExecuteAsync(null);
        Assert.Equal("anna.berger", _plainCopy);
        Assert.Null(_clipboard.Text);   // never through the sensitive clipboard
    }

    [Fact]
    public async Task Find_in_the_preview_matches_what_is_visible_and_never_a_secrets_value_even_while_held()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var preview = (await OpenAsync("Code hosting")).Preview;

        preview.FindQuery = "code";
        Assert.Equal(2, preview.FindCount);   // the name and the URL
        Assert.Contains(preview.PasswordEntry!.NameRuns, r => r.IsHit && r.IsActive);

        preview.FindQuery = "H0rse";
        Assert.Equal(0, preview.FindCount);
        var password = preview.PasswordEntry.Rows.Single(r => r.Label == Strings.Get("PwPassword"));
        password.Press();
        preview.FindQuery = "482913";   // the PIN
        Assert.Equal(0, preview.FindCount);
        Assert.DoesNotContain(password.ValueRuns, r => r.IsHit);
        password.Release();

        preview.FindQuery = Strings.Get("PwPassword");
        Assert.Equal(1, preview.FindCount);   // a label is visible text
    }

    [Fact]
    public async Task A_save_files_a_new_version_sealed_here_under_the_etag_the_entry_was_opened_with()
    {
        var entry = _store.AddEntry("Code hosting", Seal(CodeHosting));
        var opened = entry.ETag;
        var tab = await OpenAsync("Code hosting");

        await Eventually(() => tab.CanEdit);   // the entry shows a moment before its rights arrive; Edit waits for them
        tab.EditCommand.Execute(null);
        Assert.True(tab.IsEditing);
        Assert.False(tab.CanEdit);   // Save and Cancel took Edit's place
        tab.Editor!.Password = "n3w-Secret";
        await tab.Editor.SaveAsync();

        await Eventually(() => !tab.IsEditing && tab.Preview.PasswordEntry is not null);
        Assert.Equal(opened, _store.Requests.Last(r => r.Path.EndsWith("/versions/pending", StringComparison.Ordinal)).IfMatch);
        Assert.Equal("n3w-Secret", Open(entry.Content!).Password);
        Assert.True(entry.LastPasswordChanged);
        Assert.DoesNotContain("n3w-Secret", Encoding.UTF8.GetString(entry.Content!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_over_an_entry_somebody_changed_since_it_was_opened_is_said_as_such_and_the_entry_read_again()
    {
        var entry = _store.AddEntry("Code hosting", Seal(CodeHosting));
        var tab = await OpenAsync("Code hosting");
        await Eventually(() => tab.CanEdit);   // the entry shows a moment before its rights arrive; Edit waits for them
        tab.EditCommand.Execute(null);
        tab.Editor!.Password = "mine";
        _store.BeforeFinalize = e => e.Tag++;   // somebody else saved while the form was open
        var before = entry.Content;

        await tab.Editor.SaveAsync();

        Assert.Contains(Strings.Get("PwEntryChanged"), _shell.Reports);
        Assert.Same(before, entry.Content);   // nothing filed
        await Eventually(() => !tab.IsEditing && tab.Preview.PasswordEntry is not null);
        Assert.Equal(2, _keyReads);   // opened again with the key: what is on show is the version that is there
    }

    [Fact]
    public async Task A_new_entry_is_sealed_on_this_machine_with_the_pin_added_first_and_hidden()
    {
        _store.MineExists = false;   // My Passwords is created on first use
        var tab = await LoadedTabAsync();
        await Eventually(() => tab.CanCreateEntry);

        tab.NewEntryCommand.Execute(null);
        var editor = tab.Editor!;
        Assert.True(editor.SuggestsPin);
        editor.Name = "Bank";
        editor.UserName = "anna";
        editor.Password = "pa55word";
        editor.AddPinCommand.Execute(null);
        editor.Pin = "1234";
        await editor.SaveAsync();

        var stored = _store.Entry("Bank");
        Assert.Equal(0x30, stored.Content![0]);   // a bare CMS envelope reached the server, never the fields
        Assert.Equal([("User name", false), ("PIN", true)], Open(stored.Content).Fields.Select(f => (f.Name, f.IsSecret)));
        Assert.True(_store.MineExists);
    }

    // ADR 0982: the server withholds New entry from a user without an enrolled key and says why. The tab follows the
    // rel (New entry and Import disabled, New folder still offered), the disabled button's tooltip and the folder's
    // notice give the reason, and the notice offers the enrolment — after which the folder is read again.
    [Fact]
    public async Task New_entry_is_withheld_with_its_reason_until_a_key_is_enrolled_while_new_folder_stays()
    {
        _store.MineExists = true;
        _store.NewEntryUnavailableReason = "enrolment-required";
        var enrolled = 0;
        var tab = await LoadedTabAsync();
        tab.Dialogs.EnrolKey = () =>
        {
            enrolled++;
            _store.NewEntryUnavailableReason = null;   // the key is enrolled: the server offers entries again
            return Task.CompletedTask;
        };
        await Eventually(() => tab.CanCreateFolder);

        Assert.False(tab.CanCreateEntry);
        Assert.Equal(Strings.Get("PwEnrolmentRequired"), tab.NewEntryHint);
        var notice = Assert.Single(tab.Notices, n => n.Notice.Action == PasswordNoticeAction.EnrolKey);
        Assert.Equal(Strings.Get("PwEnrolmentRequired"), notice.Text);
        Assert.Equal(Strings.Get("PwEnrolKey"), notice.ActionLabel);

        tab.NewEntryCommand.Execute(null);
        Assert.Null(tab.Editor);   // no form to type a secret into that could not be saved
        Assert.DoesNotContain(_store.Requests, r => r.Method == "POST" && r.Path.EndsWith("/entries", StringComparison.Ordinal));

        await tab.NoticeActionCommand.ExecuteAsync(notice);
        await Eventually(() => tab.CanCreateEntry);
        Assert.Equal(1, enrolled);
        Assert.Equal(Strings.Get("PwNewEntry"), tab.NewEntryHint);
        Assert.DoesNotContain(tab.Notices, n => n.Notice.Action == PasswordNoticeAction.EnrolKey);
    }

    [Fact]
    public async Task A_reseal_reports_each_entry_resealed_skipped_because_changed_or_not_opened_with_this_key()
    {
        _store.AddEntry("A", Seal(CodeHosting));
        var changed = _store.AddEntry("B", Seal(CodeHosting));
        _store.AddEntry("C", PasswordStoreClient.Seal(CodeHosting, [PublicOnly(NewCertificate("Tom"))]));   // sealed before Anna was a reader
        _store.NeedsReseal = true;
        _store.BeforeFinalize = e =>
        {
            if (e == changed)
            {
                e.Tag++;
            }
        };
        string? said = null;
        var tab = await LoadedTabAsync();
        tab.Dialogs.Inform = (_, message) => { said = message; return Task.CompletedTask; };
        await Eventually(() => tab.CanReseal);

        await tab.ResealCommand.ExecuteAsync(null);

        Assert.Equal(PasswordNotices.Describe(new PasswordResealOutcome(["A"], ["B"], [("C", "x")])), said);
        Assert.Equal((int?)2, _store.Entry("A").LastCommentKind);   // filed as a re-seal, not as a change
    }

    [Fact]
    public async Task An_import_asks_only_about_the_clashing_name_seals_every_row_here_and_offers_to_delete_the_export()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var export = Path.Combine(Path.GetTempPath(), $"passwords-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(export, "name,url,username,password,note\nCode hosting,https://code.example,anna.berger,imported!,\nWiki,https://wiki.example,anna,w1ki,\n");
        var asked = new List<string>();
        var tab = await LoadedTabAsync();
        tab.Dialogs.PickExport = async () => new PickedPasswordExport("passwords.csv", await File.ReadAllTextAsync(export), export);
        tab.Dialogs.AskConflict = c => { asked.Add(c.Existing.Name); return Task.FromResult<PasswordImportResolution?>(PasswordImportResolution.KeepBoth); };
        tab.Dialogs.Inform = (_, _) => Task.CompletedTask;
        tab.Dialogs.Confirm = (_, message, _) => Task.FromResult(message == Strings.Get("PwImportDeleteCsv"));
        tab.Dialogs.DeleteFile = path => { File.Delete(path); return Task.FromResult(true); };
        await Eventually(() => tab.CanCreateEntry);

        await tab.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["Code hosting"], asked);   // Wiki clashes with nothing and is not asked about
        Assert.Equal("imported!", Open(_store.Entry("Code hosting (anna.berger)").Content!).Password);
        Assert.Equal("w1ki", Open(_store.Entry("Wiki").Content!).Password);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public async Task The_key_leaving_closes_the_entry_and_an_edit_of_it_and_a_cancelled_prompt_offers_to_open_it_again()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var tab = await OpenAsync("Code hosting");
        var password = tab.Preview.PasswordEntry!.Rows.Single(r => r.Label == Strings.Get("PwPassword"));
        password.Press();
        await Eventually(() => tab.CanEdit);   // the entry shows a moment before its rights arrive; Edit waits for them
        tab.EditCommand.Execute(null);

        tab.KeyLeft();

        Assert.Null(tab.Preview.PasswordEntry);
        Assert.Null(tab.Editor);
        Assert.True(tab.CanOpenWithKey);
        Assert.Equal([true, false], _protection);   // the held eye let go with it

        (_keyPresent, _promptCancelled) = (false, true);
        await tab.OpenWithKeyCommand.ExecuteAsync(null);
        Assert.Null(tab.Preview.PasswordEntry);
        Assert.Equal(Strings.Get("PwOpenToRead"), tab.Preview.PreviewPlaceholder);   // a cancelled prompt says nothing more

        (_keyPresent, _promptCancelled) = (true, false);
        await tab.OpenWithKeyCommand.ExecuteAsync(null);
        Assert.NotNull(tab.Preview.PasswordEntry);
    }

    // ADR 0969: an administrator's session with Anna's key in the reader reads nothing of Anna's — the key is not used.
    [Fact]
    public async Task An_entry_is_not_opened_with_a_key_enrolled_for_somebody_else()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        SignedInAs(NewCertificate("Crypt"));
        var tab = await LoadedTabAsync();
        await Eventually(() => tab.Items.Any(i => i.Name == "Code hosting"));

        tab.SelectedItem = tab.Items.Single(i => i.Name == "Code hosting");
        await Eventually(() => tab.Preview.PreviewPlaceholder?.StartsWith(Strings.Get("PvKeyNotEnrolledForYou"), StringComparison.Ordinal) == true);

        Assert.Null(tab.Preview.PasswordEntry);
        Assert.Equal(0, _decrypts);
        Assert.Equal($"{Strings.Get("PvKeyNotEnrolledForYou")}\n{Strings.Get("PvKeyNotEnrolledForYouHint")}", tab.Preview.PreviewPlaceholder);
    }

    // ADR 0619: an administrator's own My Passwords and another user's (seen through the personal-space x-ray, ADR 0670)
    // read as their parent (the personal space, named after its owner) slash their name — the key icon on the own one, the server's order.
    [Fact]
    public async Task Two_My_Passwords_are_told_apart_by_their_parent_qualified_names()
    {
        _store.MineExists = true;
        _store.OthersMyPasswords = (Guid.NewGuid(), "Bob Keller / My Passwords");

        var tab = await LoadedTabAsync();

        Assert.Equal([(FakePasswordStore.MineDisplayName, true), ("Bob Keller / My Passwords", false)], tab.Nodes.Select(n => (n.Name, n.IsPersonal)));
        Assert.Equal(FakePasswordStore.MineDisplayName, tab.SelectedNode!.Folder!.Label);
    }

    [Fact]
    public async Task Nothing_of_an_opened_entry_is_written_to_disk()
    {
        _store.AddEntry("Code hosting", Seal(CodeHosting));
        var started = DateTime.UtcNow.AddSeconds(-1);
        var tab = await OpenAsync("Code hosting");
        var password = tab.Preview.PasswordEntry!.Rows.Single(r => r.Label == Strings.Get("PwPassword"));
        password.Press();
        password.Release();

        foreach (var directory in new[] { Path.GetTempPath(), Path.Combine(Path.GetTempPath(), global::Alcifra.Localization.PersistedNames.DesktopTempFolder) }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Where(f => File.GetLastWriteTimeUtc(f) >= started))
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file);
                }
                catch (IOException)
                {
                    continue;   // held open by another process: not ours
                }

                Assert.DoesNotContain("c0rrect-H0rse!", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task An_entry_met_as_a_document_in_the_tree_opens_with_the_key_read_only()
    {
        var content = await PasswordEntryDocument.ParseAsync(Seal(CodeHosting), openedByFunnel: false, PasswordKeyOpener.Instance);
        var shown = new PasswordEntryPreviewViewModel("Code hosting", content, null, Services(), isReadOnly: true);

        Assert.True(shown.IsReadOnly);
        Assert.Equal(Strings.Get("PwUrl"), shown.Rows[0].Label);
        Assert.Equal(1, _keyReads);
    }

    // ---- the rig --------------------------------------------------------------------------------------------------

    private SecretFieldServices Services() => new(_clipboard, text => { _plainCopy = text; return Task.CompletedTask; }, _shell, _screens, _time);

    private async Task<PasswordsTabViewModel> LoadedTabAsync()
    {
        var api = new AlcifraApiClient("token");
        var tab = new PasswordsTabViewModel(_shell, () => api, Services());
        await tab.DiscoverAsync();
        Assert.True(tab.IsOffered);
        await tab.LoadAsync();
        await Eventually(() => tab.SelectedNode?.Folder is not null);
        return tab;
    }

    private async Task<PasswordsTabViewModel> OpenAsync(string name)
    {
        var tab = await LoadedTabAsync();
        await Eventually(() => tab.Items.Any(i => i.Name == name));
        tab.SelectedItem = tab.Items.Single(i => i.Name == name);
        await Eventually(() => tab.Preview.PasswordEntry is not null);
        return tab;
    }

    // The hardware key, held in memory: declines an envelope not addressed to it, as the card does.
    private Task<(byte[] Bytes, string ContentType)?> OpenWithAnnasKey(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<(string Issuer, string Serial)> recipients, Func<X509Certificate2, bool> admitted)
    {
        Interlocked.Increment(ref _keyReads);
        if (!_keyPresent || !EnvelopeRecipients.Matches(_anna, recipients) || !admitted(_anna))
        {
            return Task.FromResult<(byte[], string)?>(null);
        }

        Interlocked.Increment(ref _decrypts);

        var cms = new EnvelopedCms();
        cms.Decode(SmimeEnvelope.Cms(enveloped));
        cms.Decrypt([_anna]);
        return Task.FromResult<(byte[], string)?>(SmimeEnvelope.Payload(cms.ContentInfo.Content));
    }

    private byte[] Seal(PasswordEntryContent content) => PasswordStoreClient.Seal(content, [PublicOnly(_anna)]);

    private PasswordEntryContent Open(byte[] cms)
    {
        var envelope = new EnvelopedCms();
        envelope.Decode(cms);
        envelope.Decrypt([_anna]);
        return PasswordEntryFormat.Parse(envelope.ContentInfo.Content);
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);

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
