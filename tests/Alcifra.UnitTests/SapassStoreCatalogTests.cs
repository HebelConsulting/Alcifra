using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass;
using Alcifra.Sapass.Infrastructure;

namespace Alcifra.UnitTests;

// The flat listing hands back ids and parent ids; the tool's whole path model is the walk that turns them into
// "Mail/Banking/login". It is pure, so it is asserted directly — including the two shapes that would hang or lie
// on bad data: a row whose ancestor is missing, and a cycle.
public class SapassStoreCatalogTests
{
    private static readonly Guid Store = Guid.NewGuid();

    private static PasswordDescendantInfo Row(Guid id, string name, bool isFolder, Guid parent) =>
        new(id, name, isFolder, parent, $"/api/password-entries/{id}");

    [Fact]
    public void Paths_are_the_ancestors_names_below_the_store()
    {
        var mail = Guid.NewGuid();
        var banking = Guid.NewGuid();
        var login = Guid.NewGuid();
        var rows = PasswordTree.WithPaths(Store,
            [Row(mail, "Mail", true, Store), Row(banking, "Banking", true, mail), Row(login, "login", false, banking)]);

        Assert.Equal("Mail", rows.Single(r => r.Id == mail).Path);
        Assert.Equal("Mail/Banking", rows.Single(r => r.Id == banking).Path);
        Assert.Equal("Mail/Banking/login", rows.Single(r => r.Id == login).Path);
    }

    [Fact]
    public void A_row_whose_parent_is_missing_still_gets_a_path_instead_of_looping()
    {
        var orphan = Guid.NewGuid();
        var rows = PasswordTree.WithPaths(Store, [Row(orphan, "Loose", false, Guid.NewGuid())]);

        // The walk ends where the data ends; the row keeps the names it could prove, rather than vanishing.
        Assert.Equal("Loose", rows.Single(r => r.Id == orphan).Path);
    }

    [Fact]
    public void A_cycle_in_the_parent_ids_terminates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var rows = PasswordTree.WithPaths(Store,
            [Row(first, "A", true, second), Row(second, "B", true, first)]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Path)));
    }

    [Fact]
    public void Children_are_one_level_folders_first_then_entries_by_name()
    {
        var mail = Guid.NewGuid();
        var rows = PasswordTree.WithPaths(Store,
        [
            Row(mail, "Mail", true, Store),
            Row(Guid.NewGuid(), "zeta", false, Store),
            Row(Guid.NewGuid(), "alpha", false, Store),
            Row(Guid.NewGuid(), "Banking", true, Store),
            Row(Guid.NewGuid(), "nested", false, mail),
        ]);

        Assert.Equal(["Banking", "Mail", "alpha", "zeta"], PasswordTree.Children(rows, "").Select(r => r.Name));
        Assert.Equal(["nested"], PasswordTree.Children(rows, "Mail").Select(r => r.Name));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("  /a/b/ ", "a/b")]
    public void Paths_are_normalised_at_the_edge(string? input, string expected) =>
        Assert.Equal(expected, PasswordTree.Normalise(input));

    [Theory]
    [InlineData("a/b", "a")]
    [InlineData("a", "")]
    [InlineData("", "")]
    public void A_parent_is_the_path_without_its_last_segment(string path, string expected) =>
        Assert.Equal(expected, PasswordTree.ParentOf(path));
}

// What a write command computes without the wire: the secret it generates, and where a path splits.
public class SapassPasswordsTests
{
    [Fact]
    public void A_generated_password_has_the_length_and_no_ambiguous_characters()
    {
        var first = Passwords.Generate(24);
        var second = Passwords.Generate(24);

        Assert.Equal(24, first.Length);
        Assert.DoesNotContain(first, c => "0O1lI|".Contains(c, StringComparison.Ordinal));
        Assert.NotEqual(first, second);   // a generator that returned a constant would pass every other assertion
    }

    [Theory]
    [InlineData("Mail/Banking/login", "Mail/Banking", "login")]
    [InlineData("/Mail/login/", "Mail", "login")]
    [InlineData("login", "", "login")]
    [InlineData("Mail/Banking/", "Mail", "Banking")]   // a trailing slash normalises away: this names an ENTRY
    public void A_path_splits_into_its_folder_and_its_name(string path, string folder, string name) =>
        Assert.Equal((folder, name), Passwords.Split(path));
}

// The editor's file IS show's output: a round trip must be exact, and a malformed edit must be refused with the
// line named rather than silently losing a field.
public class SapassEntryTextTests
{
    private static readonly IReadOnlyList<PasswordEntryField> Previous =
    [
        new(PasswordEntryFieldNames.UserName, "anna", IsSecret: false),
        new("PIN", "1234", IsSecret: true),
    ];

    [Fact]
    public void Format_is_the_password_then_one_field_per_line()
    {
        var content = new PasswordEntryContent("hunter2",
            [new(PasswordEntryFieldNames.UserName, "anna"), new("PIN", "1234", IsSecret: true)]);

        Assert.Equal("hunter2\nUser name: anna\nPIN: 1234\n", EntryText.Format(content));
    }

    [Fact]
    public void A_format_parse_round_trip_keeps_values_and_the_secret_flag()
    {
        var content = new PasswordEntryContent("hunter2", [.. Previous]);
        var parsed = EntryText.Parse(EntryText.Format(content), Previous);

        Assert.Equal("hunter2", parsed.Password);
        Assert.Equal(Previous.Select(f => f.Name), parsed.Fields.Select(f => f.Name));
        Assert.Equal(Previous.Select(f => f.Value), parsed.Fields.Select(f => f.Value));
        Assert.True(parsed.Fields.Single(f => f.Name == "PIN").IsSecret);   // carried over by name, not guessed
    }

    [Fact]
    public void A_value_may_contain_the_separator()
    {
        var parsed = EntryText.Parse("pw\nURL: https://example.com:8443/x\n", []);

        Assert.Equal("https://example.com:8443/x", parsed.Fields.Single().Value);
    }

    [Fact]
    public void A_line_that_is_not_a_field_is_refused_with_its_line_number()
    {
        var refused = Assert.Throws<SapassException>(() => EntryText.Parse("pw\nUser name anna\n", Previous));

        Assert.Contains("Line 2", refused.Message);
    }

    [Fact]
    public void A_repeated_field_is_refused()
    {
        var refused = Assert.Throws<SapassException>(() => EntryText.Parse("pw\nPIN: 1\nPIN: 2\n", Previous));

        Assert.Contains("repeats", refused.Message);
    }

    [Fact]
    public void An_empty_password_line_is_refused()
    {
        Assert.Throws<SapassException>(() => EntryText.Parse("\nUser name: anna\n", Previous));
    }

    // --store by name (ADR 0619): the full "Parent / Name" always, the plain name while it is unique, the caller's own
    // store where the plain name is shared with another user's, and otherwise a refusal naming the full names.
    private static readonly PasswordStoreInfo MyOwn = new(Guid.NewGuid(), "My Passwords", true, "f/mine", "Store Admin / My Passwords");
    private static readonly PasswordStoreInfo Annas = new(Guid.NewGuid(), "My Passwords", false, "f/anna", "Anna Berger / My Passwords");
    private static readonly PasswordStoreInfo OpsTeam = new(Guid.NewGuid(), "Team", false, "f/ops", "Operations / Team");
    private static readonly PasswordStoreInfo DevTeam = new(Guid.NewGuid(), "Team", false, "f/dev", "Development / Team");
    private static readonly PasswordStoreInfo Vault = new(Guid.NewGuid(), "Vault", false, "f/vault", "Finance / Vault");
    private static readonly PasswordStoreInfo[] Stores = [MyOwn, Annas, OpsTeam, DevTeam, Vault];

    [Fact]
    public void A_store_is_found_by_its_full_name_or_its_unique_plain_name()
    {
        Assert.Same(Annas, StoreCatalog.Resolve(Stores, "Anna Berger / My Passwords"));
        Assert.Same(DevTeam, StoreCatalog.Resolve(Stores, "Development / Team"));
        Assert.Same(Vault, StoreCatalog.Resolve(Stores, "Vault"));
    }

    [Fact]
    public void A_plain_name_shared_with_the_callers_own_store_means_their_own() =>
        Assert.Same(MyOwn, StoreCatalog.Resolve(Stores, "My Passwords"));

    [Fact]
    public void An_ambiguous_plain_name_is_refused_with_the_full_names_to_choose_from()
    {
        var refused = Assert.Throws<SapassException>(() => StoreCatalog.Resolve(Stores, "Team"));
        Assert.Contains("'Operations / Team'", refused.Message);
        Assert.Contains("'Development / Team'", refused.Message);
        Assert.Contains("--store", refused.Message);
    }

    [Fact]
    public void An_unknown_name_lists_every_store_by_its_full_name() =>
        Assert.Contains("'Anna Berger / My Passwords'", Assert.Throws<SapassException>(() => StoreCatalog.Resolve(Stores, "Nope")).Message);

    [Fact]
    public void Completion_offers_a_plain_name_while_it_is_unique_and_the_full_name_where_it_repeats()
    {
        var names = StoreCatalog.CompletionNames(Stores, offersMyPasswords: true);

        Assert.Equal(["My Passwords", "Anna Berger / My Passwords", "Operations / Team", "Development / Team", "Vault"], names);
        Assert.All(names, n => StoreCatalog.Resolve(Stores, n));   // every candidate is one --store accepts
    }
}
