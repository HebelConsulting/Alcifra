using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.UnitTests.PasswordStore;

// ADR 0973: the site an entry matches on is derived from its URL slot — host only, lowercased, no leading www —
// and from any spelling of that slot, so an entry whose URL is named "Website" matches too.
public class PasswordEntrySiteTests
{
    private static PasswordEntryContent WithUrl(string url) => new("pw", [new(PasswordEntryFieldNames.Url, url)]);

    [Theory]
    [InlineData("https://bank.example/login?next=/", "bank.example")]
    [InlineData("https://www.bank.example", "bank.example")]
    [InlineData("http://BANK.example", "bank.example")]
    [InlineData("bank.example/login", "bank.example")]          // the scheme is optional, as users type it
    [InlineData("www.bank.example", "bank.example")]
    [InlineData("  https://bank.example  ", "bank.example")]    // trimmed
    public void The_site_is_the_host_lowercased_without_www(string url, string expected) =>
        Assert.Equal(expected, WithUrl(url).Site);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url with spaces")]
    public void No_host_means_no_site(string url) => Assert.Null(WithUrl(url).Site);

    [Fact]
    public void An_entry_without_a_url_has_no_site() =>
        Assert.Null(new PasswordEntryContent("pw", []).Site);

    [Theory]
    [InlineData("Website")]
    [InlineData("Link")]
    [InlineData("web site")]
    public void Any_spelling_of_the_url_slot_names_the_site(string fieldName) =>
        Assert.Equal("bank.example", new PasswordEntryContent("pw", [new(fieldName, "https://bank.example")]).Site);

    [Fact]
    public void The_app_package_is_read_from_the_app_field() =>
        Assert.Equal("com.bank.app", new PasswordEntryContent("pw", [new(PasswordEntryFieldNames.App, "com.bank.app")]).App);

    [Fact]
    public void No_app_field_means_no_app() => Assert.Null(new PasswordEntryContent("pw", []).App);
}
