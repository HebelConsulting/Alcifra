using System.Net;
using System.Text;
using Alcifra.Localization;
using Alcifra.Theming;

namespace Alcifra.Api.Download;

/// <summary>
/// The public download area (ADR 0490): ONLY <c>/download</c> is browsable, so a visitor can click through to
/// <c>clients/&lt;os&gt;/</c> and take the build that matches this API, and open the manual.
/// </summary>
/// <remarks>
/// The win/linux archives are baked into the image by the Dockerfile; a Linux image cannot build the .dmg, so the macOS
/// page links to the brand's release repository instead. Both pages a person reads, the manual's and the macOS
/// client's, are rendered here from the brand rather than shipped as files, and the manual's file name follows the
/// brand's file stem; any other manual name redirects to it (ADR 0997).
/// </remarks>
public static class DownloadArea
{
    /// <summary>
    /// Adds the download area. Placed BEFORE authentication, so the listing and the downloads are anonymous.
    /// </summary>
    /// <remarks>
    /// Uses a real <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/>: directory browsing needs
    /// enumeration, which the static-web-assets manifest provider used under <c>dotnet run</c> does not support. The
    /// dedicated static-file handler sets ServeUnknownFileTypes so .dmg/.tar.gz download rather than 404.
    /// </remarks>
    public static void UseDownloadArea(this WebApplication app)
    {
        var downloadDir = new[]
        {
            string.IsNullOrEmpty(app.Environment.WebRootPath) ? null : Path.Combine(app.Environment.WebRootPath, "download"),
            Path.Combine(app.Environment.ContentRootPath, "wwwroot", "download"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "download"),
            // dev: `dotnet run` sets the content root to the repo root, so the API's own wwwroot is under src/.
            Path.Combine(app.Environment.ContentRootPath, "src", "Alcifra.Api", "wwwroot", "download"),
        }.FirstOrDefault(p => p is not null && Directory.Exists(p));

        if (downloadDir is null)
        {
            return;
        }

        app.Use(ServeBrandedAsync);

        var downloadProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(downloadDir);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = downloadProvider, RequestPath = "/download" });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = downloadProvider,
            RequestPath = "/download",
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = downloadProvider,
            RequestPath = "/download",
            // The product's own design rather than the framework's "Index of /download/…" (ADR 0578). The
            // listing stays dynamic because archive filenames carry a version that changes every release.
            Formatter = new ThemedDirectoryFormatter(),
        });
    }

    private static async Task ServeBrandedAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await next(context);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        var manual = $"/download/manual/{Brand.Shipped.ManualFileName}";
        if (IsAnotherManualName(path, manual))
        {
            // Temporary, not permanent: a cached permanent redirect would outlive a later change of the stem.
            context.Response.Redirect(manual);
            return;
        }

        var page = path switch
        {
            "/download/manual/" or "/download/manual/index.html" => ManualPage(),
            "/download/clients/macos/" or "/download/clients/macos/index.html" => MacOsPage(),
            _ => null,
        };

        if (page is null)
        {
            await next(context);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsGet(request.Method))
        {
            await context.Response.WriteAsync(page, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> asks for the manual under a name that is not this server's: its name before it
    /// followed the brand (<c>Alcifra-Manual.pdf</c>, old links and bookmarks), or another edition's, which a
    /// desktop client of that brand asks for when connected here.
    /// </summary>
    internal static bool IsAnotherManualName(string path, string manual) =>
        path.StartsWith("/download/manual/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("-Manual.pdf", StringComparison.OrdinalIgnoreCase)
        && path.IndexOf('/', "/download/manual/".Length) < 0
        && !string.Equals(path, manual, StringComparison.Ordinal);

    private static string ManualPage()
    {
        var title = $"{Strings.ProductName} — User Manual";
        var file = WebUtility.HtmlEncode(Brand.Shipped.ManualFileName);
        return DownloadPage.Render(title, $"""
              <h1>{WebUtility.HtmlEncode(title)}</h1>
              <p>A concise, screenshot-led guide to both clients — the web workbench and the desktop application.</p>
              <p><a class="btn" href="{file}">Open the manual (PDF) →</a></p>
              <p class="muted">
                The manual's screenshots are regenerated from the running application on every release, so they never
                fall out of date. Desktop-client downloads are available under <a href="../clients/">download/clients/</a>.
              </p>
            """);
    }

    private static string MacOsPage()
    {
        var title = $"{Strings.ProductName} desktop client — macOS";
        var releases = WebUtility.HtmlEncode($"https://github.com/{Brand.Shipped.Distribution.ReleaseRepository}/releases/latest");
        return DownloadPage.Render(title, $"""
              <h1>{WebUtility.HtmlEncode(title)}</h1>
              <p>
                The macOS <code>.dmg</code> installers (Apple&nbsp;Silicon <code>arm64</code> and Intel <code>x64</code>)
                are attached to each release — a macOS build can't be produced by the Linux server image that hosts this page.
              </p>
              <p><a class="btn" href="{releases}">Download the latest release →</a></p>
              <p class="muted">
                The <code>.dmg</code> is self-contained (no .NET needed). If macOS warns on first open, right-click the
                app and choose Open. Windows and Linux builds are available one level up under <a href="../">clients/</a>.
              </p>
            """);
    }
}
