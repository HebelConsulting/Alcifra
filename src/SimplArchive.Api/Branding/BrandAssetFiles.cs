using Microsoft.Extensions.FileProviders;

namespace SimplArchive.Api.Branding;

/// <summary>
/// Serves <c>brand/assets</c> at <c>/brand</c> (ADRs 0991, 0992) and the generated icons (ADR 0993): the web fonts <c>/api/theme.css</c> names and the
/// wordmark the pages show. Anonymous, like the stylesheet, because the sign-in page needs them before anyone has a
/// token.
/// </summary>
/// <remarks>
/// The files live in <c>brand/assets</c>, outside this project, so a white-label build changes <c>brand/</c>
/// alone. Publishing copies them into <c>wwwroot/brand</c>; a source tree (<c>dotnet run</c>, the browser suites)
/// has them only in <c>brand/</c>. They are deliberately NOT static web assets: under <c>dotnet run</c> those map a
/// linked file to the project's own wwwroot, where it does not exist, and every font answered 404 while the
/// published build was fine. So the folder is resolved here, the way the download area resolves its own.
/// </remarks>
public static class BrandAssetFiles
{
    /// <summary>
    /// The installation's own favicon, then the brand's assets at <c>/brand</c> and, in a source tree, its generated
    /// icons (ADR 0993). Before the shipped static files, because static files serve the first match.
    /// </summary>
    public static IApplicationBuilder UseBrandAssets(this WebApplication app)
    {
        // First, so an operator's custom/favicon.* wins over the brand's.
        app.UseCustomFavicon();

        if (Locate(app.Environment) is { } directory)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(directory),
                RequestPath = "/brand",
                // An asset changes only with a release, and its URL does not; a day keeps repeat visits cheap
                // without pinning a rebrand for longer than that.
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "public, max-age=86400",
            });
        }
        else
        {
            app.Logger.LogWarning(
                "The brand's assets were not found (wwwroot/brand or brand/assets); pages fall back to the next font and to the name as text.");
        }

        // Published, the generated icons were copied into wwwroot and the shipped static files serve them. In a source
        // tree they exist only where the build wrote them, so they are served from there, at the root like wwwroot.
        if (!PublishedIcons(app.Environment) && FindUp(app.Environment, Path.Combine("artifacts", "brand-icons", "web")) is { } icons)
        {
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(icons) });
        }

        return app;
    }

    private static bool PublishedIcons(IWebHostEnvironment environment) =>
        !string.IsNullOrEmpty(environment.WebRootPath) && File.Exists(Path.Combine(environment.WebRootPath, "favicon.png"));

    private static string? Locate(IWebHostEnvironment environment)
    {
        var published = string.IsNullOrEmpty(environment.WebRootPath) ? null : Path.Combine(environment.WebRootPath, "brand");
        if (published is not null && Directory.Exists(published))
        {
            return published;
        }

        return FindUp(environment, Path.Combine("brand", "assets"));
    }

    // A source tree: walk up from wherever the app was started until the relative folder appears.
    private static string? FindUp(IWebHostEnvironment environment, string relative)
    {
        foreach (var start in new[] { environment.ContentRootPath, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
