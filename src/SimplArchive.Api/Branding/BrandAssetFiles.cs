using Microsoft.Extensions.FileProviders;

namespace SimplArchive.Api.Branding;

/// <summary>
/// Serves <c>brand/assets</c> at <c>/brand</c> (ADRs 0991, 0992): the web fonts <c>/api/theme.css</c> names and the
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
    /// <summary>The brand's static files and the installation's own favicon, before the shipped static files.</summary>
    public static IApplicationBuilder UseBrandAssets(this WebApplication app)
    {
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

        return app.UseCustomFavicon();
    }

    private static string? Locate(IWebHostEnvironment environment)
    {
        var published = string.IsNullOrEmpty(environment.WebRootPath) ? null : Path.Combine(environment.WebRootPath, "brand");
        if (published is not null && Directory.Exists(published))
        {
            return published;
        }

        // A source tree: walk up from wherever the app was started until brand/ appears.
        foreach (var start in new[] { environment.ContentRootPath, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "brand", "assets");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
