using SimplArchive.Presentation;

namespace SimplArchive.UnitTests.PasswordStore;

// What opens as a web page (core #1949, owner decisions): http and https only, and a host typed without a scheme as
// https. A link never runs a script, opens a file or starts another app.
public sealed class WebLinksTests
{
    [Theory]
    [InlineData("https://bank.example/login", "https://bank.example/login")]
    [InlineData("http://intranet.example", "http://intranet.example/")]
    [InlineData("  https://bank.example  ", "https://bank.example/")]
    [InlineData("bank.example", "https://bank.example/")]
    [InlineData("www.bank.example/login?x=1", "https://www.bank.example/login?x=1")]
    [InlineData("localhost:8080", "https://localhost:8080/")]
    [InlineData("demo.simplarchive.dev:8443/path", "https://demo.simplarchive.dev:8443/path")]
    public void A_web_address_as_people_type_it_opens(string typed, string opened) =>
        Assert.Equal(new Uri(opened), WebLinks.Typed(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("n/a")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("mailto:anna@example.com")]
    [InlineData("tel:+41791234567")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example")]
    [InlineData("intent://scan#Intent;scheme=zxing;end")]
    [InlineData("bank.example and more")]
    public void Anything_else_is_no_link(string? typed) => Assert.Null(WebLinks.Typed(typed));

    [Theory]
    [InlineData("https://bank.example", true)]
    [InlineData("bank.example", false)]
    [InlineData("javascript:alert(1)", false)]
    public void An_index_fields_url_must_be_absolute(string value, bool opens) =>
        Assert.Equal(opens, WebLinks.Absolute(value) is not null);
}
