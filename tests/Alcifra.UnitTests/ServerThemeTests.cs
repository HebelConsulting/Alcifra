using System.Net;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.Theming;

namespace Alcifra.UnitTests;

// #2319 / Mobile #294: a client reads the palette an installation wears from its anonymous `theme` rel, follows it
// from the API root, and answers nothing — never an error — where the server cannot be read. A colour scheme is
// never worth failing a sign-in over, and the caller keeps the design it was built with.
public sealed class ServerThemeTests
{
    private const string Server = "https://archive.example";

    [Fact]
    public async Task The_palette_is_followed_from_the_roots_theme_rel()
    {
        var theme = ShippedVariant("#123456");
        var http = new Scripted(
            ("https://archive.example/api", $$"""{"links":[{"rel":"self","href":"/api"},{"rel":"theme","href":"/api/theme"}]}"""),
            ("https://archive.example/api/theme", theme));

        var tokens = await new ServerTheme(new HttpClient(http)).ReadAsync(Server);

        Assert.Equal("#123456", tokens!.Light.Accent.Primary);
        Assert.Equal(["https://archive.example/api", "https://archive.example/api/theme"], http.Requested);
    }

    [Fact]
    public async Task An_absolute_href_is_followed_as_it_is()
    {
        var http = new Scripted(
            ("https://archive.example/api", $$"""{"links":[{"rel":"theme","href":"https://themes.example/alcifra.json"}]}"""),
            ("https://themes.example/alcifra.json", ShippedVariant("#123456")));

        var tokens = await new ServerTheme(new HttpClient(http)).ReadAsync(Server);

        Assert.Equal("#123456", tokens!.Light.Accent.Primary);
    }

    [Fact]
    public async Task A_server_that_advertises_no_theme_rel_has_no_palette()
    {
        var http = new Scripted(
            ("https://archive.example/api", """{"links":[{"rel":"self","href":"/api"}]}"""));

        Assert.Null(await new ServerTheme(new HttpClient(http)).ReadAsync(Server));
        Assert.Equal(["https://archive.example/api"], http.Requested);
    }

    [Fact]
    public async Task An_unreachable_server_answers_nothing_rather_than_an_error()
    {
        var http = new Fails();

        Assert.Null(await new ServerTheme(new HttpClient(http)).ReadAsync(Server));
    }

    [Fact]
    public async Task A_palette_that_fails_contrast_is_refused_and_the_built_in_design_stands()
    {
        // White on white: the primary button's label would be invisible. The server applies the same standard to its
        // own custom theme (ADR 0578), so a client does not paint what the server would have refused.
        var tokens = ThemeTokensReader.Shipped with
        {
            Light = ThemeTokensReader.Shipped.Light with
            {
                Accent = ThemeTokensReader.Shipped.Light.Accent with { Primary = "#FFFFFF", OnPrimary = "#FFFFFF" },
            },
        };
        var http = new Scripted(
            ("https://archive.example/api", """{"links":[{"rel":"theme","href":"/api/theme"}]}"""),
            ("https://archive.example/api/theme", JsonSerializer.Serialize(tokens)));

        Assert.Null(await new ServerTheme(new HttpClient(http)).ReadAsync(Server));
    }

    // A server's answer in the shape ThemeController returns: the whole effective palette, both themes stated.
    private static string ShippedVariant(string primary) => JsonSerializer.Serialize(
        ThemeTokensReader.Load(JsonSerializer.Serialize(new { light = new { accent = new { primary } } })).Tokens);

    private sealed class Scripted(params (string Url, string Body)[] answers) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var answer = answers.FirstOrDefault(a => a.Url == url);
            return Task.FromResult(answer.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Fails : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Name or service not known");
    }
}
