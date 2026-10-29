using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.UnitTests.PasswordStore;

// The Passwords screens' rules both native clients answer from the core (ADR 0968, moved from Mobile ADR 0037's
// Mobile.Core): the rows of an opened entry in the core's order, the secret field, the screen protection's count, the
// form, the notices, the store list and the generator's typed length.
public sealed class PasswordScreenRulesTests
{
    private static readonly PasswordEntryContent Entry = new("pw-123",
    [
        new PasswordEntryField(PasswordEntryFieldNames.UserName, "alice"),
        new PasswordEntryField("PIN", "4711", IsSecret: true),
        new PasswordEntryField(PasswordEntryFieldNames.Notes, "a note"),
        new PasswordEntryField(PasswordEntryFieldNames.Url, "https://example.com"),
    ]);

    // Owner decision 2026-10-23 (ADR 0965): URL, user name, password, PIN, then the rest in the entry's own order.
    [Fact]
    public void The_fields_come_in_the_cores_order_and_the_password_and_marked_fields_are_hidden()
    {
        var rows = PasswordEntryRows.Of(Entry);

        Assert.Equal([Strings.Get("PwUrl"), Strings.Get("PwUserName"), Strings.Get("PwPassword"), Strings.Get("MobKeyPin"), Strings.Get("PwNotes")], rows.Select(r => r.Label));
        Assert.Equal([false, false, true, true, false], rows.Select(r => r.IsSecret));
        Assert.True(rows[2].IsPassword);
        Assert.True(rows[^1].IsMultiline);
    }

    // #1949: the URL's row opens in the browser; no other row does, and a URL marked secret is no link.
    [Fact]
    public void Only_the_urls_row_is_a_link()
    {
        var rows = PasswordEntryRows.Of(Entry);

        Assert.Equal([new Uri("https://example.com")], rows.Where(r => r.Link is not null).Select(r => r.Link));
        Assert.Null(PasswordEntryRows.Of(new PasswordEntryContent("pw", [new PasswordEntryField(PasswordEntryFieldNames.Url, "https://example.com", IsSecret: true)]))
            .Single(r => r.FieldName == PasswordEntryFieldNames.Url).Link);
    }

    [Fact]
    public void An_entry_without_a_slot_skips_it_and_an_ordinary_field_named_like_the_password_keeps_its_place_after_it()
    {
        var rows = PasswordEntryRows.Of(new PasswordEntryContent("pw", [new PasswordEntryField("Recovery", "r"), new PasswordEntryField("Password", "old")]));

        Assert.Equal([Strings.Get("PwPassword"), "Recovery", "Password"], rows.Select(r => r.Label));
        Assert.Equal(["pw", "r", "old"], rows.Select(r => r.Value));
    }

    [Fact]
    public void A_secret_shows_only_while_the_eye_is_held()
    {
        var field = new SecretField(PasswordEntryRows.Of(Entry)[2], new FakeClipboard());

        Assert.Equal(SecretFields.Mask, field.Display);
        field.Reveal.Press();
        Assert.Equal("pw-123", field.Display);
        field.Reveal.Release();
        Assert.Equal(SecretFields.Mask, field.Display);
        field.Reveal.Press();
        field.Hide();   // the view went away while it was held
        Assert.Equal(SecretFields.Mask, field.Display);
    }

    [Fact]
    public async Task A_copied_secret_is_cleared_after_thirty_seconds_and_a_plain_field_is_copied_and_left()
    {
        var time = new ManualTime();
        var clipboard = new FakeClipboard();
        var copying = new SecretField(PasswordEntryRows.Of(Entry)[2], clipboard, time).CopyAsync(_ => Task.CompletedTask);
        Assert.True(clipboard.Sensitive);
        time.Advance(SecretFields.ClipboardLifetime);
        Assert.True(await copying);
        Assert.Null(clipboard.Text);

        string? copied = null;
        Assert.False(await new SecretField(PasswordEntryRows.Of(Entry)[1], clipboard).CopyAsync(text => { copied = text; return Task.CompletedTask; }));
        Assert.Equal("alice", copied);
        Assert.Null(clipboard.Text);   // never through the sensitive clipboard
    }

    [Fact]
    public void Protection_lifts_only_when_the_last_view_showing_secrets_went()
    {
        var applied = new List<bool>();
        var screens = new SecretScreens(applied.Add);

        var entry = screens.Showing();
        var edit = screens.Showing();
        entry.Dispose();
        Assert.True(screens.Protected);
        edit.Dispose();
        edit.Dispose();   // twice is once

        Assert.Equal([true, false], applied);
        Assert.False(screens.Protected);
    }

    [Fact]
    public void An_edit_keeps_every_field_and_saves_the_form_in_the_entry_format()
    {
        var content = new PasswordEntryContent("pw",
        [
            new PasswordEntryField("PIN", "4711", IsSecret: true),
            new PasswordEntryField(PasswordEntryFieldNames.UserName, "alice"),
            new PasswordEntryField(PasswordEntryFieldNames.Url, "https://a.example"),
            new PasswordEntryField(PasswordEntryFieldNames.Url, "https://b.example"),
            new PasswordEntryField(PasswordEntryFieldNames.Notes, "note"),
        ]);

        var form = PasswordEntryForm.From(content);
        var saved = form.ToContent();

        Assert.Equal(("alice", "https://a.example", "4711", "note"), (form.UserName, form.Url, form.Pin, form.Notes));
        Assert.Equal([PasswordEntryFieldNames.Url], form.Fields.Select(f => f.Name));   // the second URL stays a field
        Assert.Equal(content.Fields.OrderBy(f => f.Name + f.Value), saved.Fields.OrderBy(f => f.Name + f.Value));
    }

    [Theory]
    [InlineData("", "x", "ApiErrPasswordItemNameRequired")]
    [InlineData("Site", "a:b", "PwFieldNameInvalid")]
    [InlineData("Site", "Code [secret]", "PwFieldNameInvalid")]
    [InlineData("Site", "Code", null)]
    public void A_form_says_why_it_cannot_be_saved(string name, string fieldName, string? problem)
    {
        var form = new PasswordEntryForm { Name = name, Password = "pw" };
        form.Fields.Add(new PasswordFormField(fieldName, "v"));

        Assert.Equal(problem, form.Problem(creating: true));
    }

    // Owner, 2026-10-23: the PIN is not offered unasked; "Add field" suggests it first; once there it is hidden.
    [Fact]
    public void The_PIN_is_suggested_until_added_or_present_and_is_always_filed_hidden()
    {
        var empty = new PasswordEntryForm();
        Assert.True(empty.SuggestsPin);
        empty.AddPin();
        Assert.False(empty.SuggestsPin);

        var existing = PasswordEntryForm.From(new PasswordEntryContent("pw", [new PasswordEntryField("Pin", "0000")]));
        Assert.True(existing.HasPin);
        Assert.Equal([("Pin", true)], existing.ToContent().Fields.Select(f => (f.Name, f.IsSecret)));
    }

    [Fact]
    public void My_Passwords_comes_first_and_is_offered_before_it_exists()
    {
        var shared = new PasswordStoreInfo(Guid.NewGuid(), "Team", false, "api/password-folders/t");
        var other = new PasswordStoreInfo(Guid.NewGuid(), "alpha", false, "api/password-folders/a");

        var rows = PasswordStoreList.Rows([shared, other], offersMyPasswords: true);

        Assert.Equal([Strings.Get("PwMyPasswords"), "alpha", "Team"], rows.Select(r => r.Name));
        Assert.Null(rows[0].Href);   // created when first opened
    }

    // ADR 0619: a row shows the server's parent-qualified name, so an administrator's own My Passwords and another
    // user's (the personal-space x-ray, ADR 0670) are told apart; an older server sends none and the plain name is shown.
    [Fact]
    public void A_store_row_shows_the_parent_qualified_name_and_the_plain_name_from_an_older_server()
    {
        var mine = new PasswordStoreInfo(Guid.NewGuid(), "My Passwords", true, "api/password-folders/m", "Store Admin / My Passwords");
        var annas = new PasswordStoreInfo(Guid.NewGuid(), "My Passwords", false, "api/password-folders/a", "Anna Berger / My Passwords");
        var team = new PasswordStoreInfo(Guid.NewGuid(), "Team", false, "api/password-folders/t", "Operations / Team");
        var older = new PasswordStoreInfo(Guid.NewGuid(), "Archive", false, "api/password-folders/o");

        var rows = PasswordStoreList.Rows([team, older, annas, mine], offersMyPasswords: true);

        Assert.Equal(["Store Admin / My Passwords", "Anna Berger / My Passwords", "Archive", "Operations / Team"], rows.Select(r => r.Name));
        Assert.True(rows[0].IsPersonal);
    }

    [Theory]
    [InlineData(true, true, "PwUrgentReseal", true)]
    [InlineData(true, false, "PwUrgentReseal", false)]
    [InlineData(false, true, "PwNeedsReseal", true)]
    public void A_store_that_needs_resealing_says_so_and_offers_it_only_to_the_member_who_may(bool urgent, bool mayReseal, string key, bool offered)
    {
        var notice = PasswordNotices.For(Folder(needsReseal: true, urgent: urgent, certificates: 2, mayReseal: mayReseal, personal: false)).Single();

        Assert.Equal(Strings.Get(key), notice.Text);
        Assert.Equal(urgent, notice.Urgent);
        Assert.Equal(offered ? PasswordNoticeAction.Reseal : PasswordNoticeAction.None, notice.Action);
    }

    [Fact]
    public void My_Passwords_sealed_to_one_key_recommends_a_spare_and_a_store_without_keys_says_nothing_can_be_saved()
    {
        Assert.Equal(PasswordNoticeAction.EnrolSpareKey, PasswordNotices.For(Folder(certificates: 1, personal: true)).Single().Action);
        Assert.Empty(PasswordNotices.For(Folder(certificates: 2, personal: true)));
        Assert.Equal(Strings.Get("PwNoRecipients"),
            PasswordNotices.For(Folder(certificates: 0, personal: false, withheld: "no-reader-certificates")).Single().Text);
    }

    // ADR 0982: the server withholds New entry and says why; every client shows the same sentence, and only the missing
    // enrolment offers an action. The rel decides: a reason beside an advertised rel is ignored, and a reason this client
    // does not know still explains the withheld rel generically.
    [Theory]
    [InlineData("enrolment-required", NewEntryUnavailability.EnrolmentRequired, "PwEnrolmentRequired", PasswordNoticeAction.EnrolKey)]
    [InlineData("not-a-reader", NewEntryUnavailability.NotAReader, "PwNotAReader", PasswordNoticeAction.None)]
    [InlineData("no-reader-certificates", NewEntryUnavailability.NoReaderCertificates, "PwNoRecipients", PasswordNoticeAction.None)]
    [InlineData("something-newer", NewEntryUnavailability.Unknown, "PwNewEntryUnavailable", PasswordNoticeAction.None)]
    public void A_withheld_new_entry_says_why(string reason, NewEntryUnavailability expected, string key, PasswordNoticeAction action)
    {
        var folder = Folder(certificates: 1, personal: false, withheld: reason);

        Assert.False(folder.CanCreateEntries);
        Assert.True(folder.CanCreateFolders);
        Assert.Equal(expected, folder.NewEntryUnavailable);
        Assert.Equal(Strings.Get(key), PasswordNotices.WhyNoNewEntry(folder));
        var notice = PasswordNotices.For(folder).Single();
        Assert.Equal((Strings.Get(key), action), (notice.Text, notice.Action));
    }

    [Fact]
    public void An_offered_new_entry_has_no_reason_and_no_notice()
    {
        var folder = Folder(personal: false);

        Assert.True(folder.CanCreateEntries);
        Assert.Equal(NewEntryUnavailability.None, folder.NewEntryUnavailable);
        Assert.Null(PasswordNotices.WhyNoNewEntry(folder));
        Assert.Empty(PasswordNotices.For(folder));
    }

    [Fact]
    public async Task Creating_an_entry_where_the_rel_is_withheld_refuses_before_sealing_or_sending()
    {
        using var http = new HttpClient(new RefusingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var client = new PasswordStoreClient(ApiCore.Over(http));

        var refused = await Assert.ThrowsAsync<PasswordEntryCreationWithheldException>(() =>
            client.CreateEntryAsync(Folder(withheld: "enrolment-required"), "Bank", PasswordEntryContent.Of("pw")));

        Assert.Equal(NewEntryUnavailability.EnrolmentRequired, refused.Reason);
        Assert.Contains("Enrol a hardware key", refused.Message);
    }

    // Every request fails the test: the refusal must come before the client asks the server anything.
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"No request was expected, but {request.Method} {request.RequestUri} was sent.");
    }

    [Fact]
    public void A_length_the_classes_cannot_fill_says_the_range_that_works_and_a_typed_one_makes_the_password()
    {
        Assert.Equal(24, PasswordGeneration.Generate(" 24 ", new PasswordGeneratorOptions(Symbols: false)).Password!.Length);
        Assert.Equal(string.Format(Strings.Get("PwGenerateLengthRange"), 4, PasswordGenerator.MaximumLength), PasswordGeneration.Generate("2", new PasswordGeneratorOptions()).Problem);
        Assert.Equal(Strings.Get("PwGenerateNoClass"), PasswordGeneration.Generate("20", new PasswordGeneratorOptions(20, false, false, false, false)).Problem);
    }

    [Fact]
    public void A_password_entry_is_known_by_its_seeded_mask_name()
    {
        Assert.True(PasswordEntryDocument.Is("Password Entry"));
        Assert.False(PasswordEntryDocument.Is("Password Folder"));
        Assert.False(PasswordEntryDocument.Is(null));
    }

    private static PasswordFolderInfo Folder(
        bool needsReseal = false, bool urgent = false, int certificates = 2, bool mayReseal = false, bool personal = false, string? withheld = null)
    {
        var links = new Dictionary<string, string> { ["self"] = "api/password-folders/x", ["password-folders"] = "api/x/folders" };
        if (withheld is null)
        {
            links["password-entries"] = "api/x/entries";
        }

        if (mayReseal)
        {
            links["reseal"] = "api/x/reseal";
        }

        return new PasswordFolderInfo(Guid.NewGuid(), "Store", true, Guid.NewGuid(), personal, needsReseal, urgent, certificates, "\"1\"", [], LinkMap.FromHrefs(links), withheld);
    }
}
