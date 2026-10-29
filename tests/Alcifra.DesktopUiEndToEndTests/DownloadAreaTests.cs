using System.Net;
using Alcifra.Localization;
using Alcifra.Theming;

namespace Alcifra.UiEndToEndTests;

// The desktop-client download area (ADR 0490): /download is directory-browsable so a visitor can click through to
// clients/<os>/, but the rest of the served static content (the SPA / wwwroot) must NOT be browsable. Guards
// against UseDirectoryBrowser being mis-scoped to the whole web root. Plain HTTP against the self-hosted app.
[Collection(UiCollection.Name)]
public class DownloadAreaTests
{
    private readonly SelfHostedAppFixture _app;

    public DownloadAreaTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Only_the_download_folder_is_directory_browsable()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        // Browse like a browser — UseDirectoryBrowser only renders a listing for a client that accepts text/html.
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        // /download IS browsable — the generated listing shows the clients/ subfolder.
        var download = await http.GetAsync("/download/");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var downloadBody = await download.Content.ReadAsStringAsync();
        Assert.Contains("clients", downloadBody);

        // A subfolder is browsable too (windows/ has no index.html → a listing, not the SPA).
        var windows = await http.GetAsync("/download/clients/windows/");
        Assert.Equal(HttpStatusCode.OK, windows.StatusCode);
        Assert.DoesNotContain("<div id=\"app\">", await windows.Content.ReadAsStringAsync());

        // The rest of the static content is NOT browsable: the web-root request returns the Blazor SPA shell, not a
        // directory listing of wwwroot. If UseDirectoryBrowser were mis-scoped to "", GET / would be a listing and
        // this SPA marker would be absent — so this is the real guard.
        var root = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("<div id=\"app\">", await root.Content.ReadAsStringAsync());

        // A non-/download static directory is not browsable either — it falls through to the SPA fallback, so it
        // never enumerates the framework files.
        var framework = await http.GetAsync("/_framework/");
        Assert.DoesNotContain("blazor.boot.json", await framework.Content.ReadAsStringAsync());
    }

    // The auto-generated user manual (ADR 0502) is baked into wwwroot/download/manual/ and must be served
    // anonymously as a real PDF — the /download handler sets ServeUnknownFileTypes, so the content type matters.
    // Its name is the brand's file stem (ADR 0997).
    [Fact]
    public async Task Serves_the_generated_user_manual()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };

        var manual = await http.GetAsync($"/download/manual/{Brand.Shipped.ManualFileName}");
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.Equal("application/pdf", manual.Content.Headers.ContentType?.MediaType);

        var bytes = await manual.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4)); // a real PDF, not an error page
    }

    // Old links and bookmarks, and a desktop client of another edition, ask for the manual under another name; the
    // server answers with its own (ADR 0997). Temporary, so a later change of the stem is not cached against.
    [Theory]
    [InlineData($"/download/manual/{Alcifra.Localization.PersistedNames.EarlierProduct}-Manual.pdf")]
    [InlineData("/download/manual/Another-Edition-Manual.pdf")]
    public async Task Another_manual_name_redirects_to_this_servers_manual(string path)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(_app.BaseUrl) };

        var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/download/manual/{Brand.Shipped.ManualFileName}", response.Headers.Location?.OriginalString);
    }

    // The pages a person reads name the product and are rendered from the brand rather than shipped as files, which
    // would say one edition's name in every edition (ADR 0997).
    [Fact]
    public async Task The_manual_and_macOS_pages_follow_the_brand()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        var manual = await http.GetStringAsync("/download/manual/");
        Assert.Contains($"<title>{Strings.ProductName} — User Manual</title>", manual);
        Assert.Contains($"href=\"{Brand.Shipped.ManualFileName}\"", manual);

        var macOs = await http.GetStringAsync("/download/clients/macos/");
        Assert.Contains($"<title>{Strings.ProductName} desktop client — macOS</title>", macOs);
        Assert.Contains($"https://github.com/{Brand.Shipped.Distribution.ReleaseRepository}/releases/latest", macOs);

        // The listing's title once printed its own interpolation braces, because the raw string had no `$`.
        var listing = await http.GetStringAsync("/download/");
        Assert.Contains($"<title>{Strings.ProductName} — downloads</title>", listing);
        Assert.DoesNotContain("HtmlEncode", listing);
    }
}
