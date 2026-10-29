using System.Net;
using SimplArchive.Localization;

namespace SimplArchive.Api.Download;

/// <summary>
/// The one page shell of the <c>/download</c> area: the listings and the two pages written for a reader, the
/// manual's and the macOS client's (ADRs 0578, 0997). Rendered rather than shipped as HTML files, because each page
/// names the product, and a file in the tree would say one brand's name in every edition.
/// </summary>
internal static class DownloadPage
{
    /// <summary>A whole page around <paramref name="bodyHtml"/>, which the caller has already encoded.</summary>
    public static string Render(string title, string bodyHtml)
    {
        var product = WebUtility.HtmlEncode(Strings.ProductName);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="robots" content="noindex, nofollow">
              <title>{{WebUtility.HtmlEncode(title)}}</title>
              <link rel="stylesheet" href="/api/theme.css">
              <style>
                body { font-family: var(--sa-font-ui, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif);
                       max-width: 640px; margin: 3rem auto; padding: 0 1rem; line-height: 1.5;
                       background: var(--sa-canvas, #FAFAFC); color: var(--sa-text, #14161C); }
                h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
                .path { color: var(--sa-text-secondary, #5A5F6E); font-size: .9rem; margin: 0 0 1.5rem; }
                ul { list-style: none; padding: 0; margin: 0; border-top: 1px solid var(--sa-hairline, #E6E7EC); }
                li { border-bottom: 1px solid var(--sa-hairline, #E6E7EC); }
                a.row { display: flex; justify-content: space-between; gap: 1rem; align-items: baseline;
                        padding: .7rem .25rem; text-decoration: none; color: inherit; }
                a.row:hover { background: var(--sa-accent-tint, #E4F3F1); }
                .name { overflow-wrap: anywhere; }
                .dir .name { font-weight: 600; color: var(--sa-accent-text, #0F766E); }
                .size { color: var(--sa-text-secondary, #5A5F6E); font-size: .85rem; white-space: nowrap;
                        font-variant-numeric: tabular-nums; }
                .empty { color: var(--sa-text-secondary, #5A5F6E); padding: 1rem .25rem; }
                a.btn { display: inline-block; background: var(--sa-accent, #0F766E); color: var(--sa-on-accent, #fff);
                        padding: .6rem 1rem; border-radius: 6px; text-decoration: none; font-weight: 600; }
                code { background: var(--sa-hairline, #E6E7EC); padding: .1rem .35rem; border-radius: 4px; }
                .muted { color: var(--sa-text-secondary, #5A5F6E); font-size: .9rem; }
                footer { margin-top: 2rem; font-size: .85rem; color: var(--sa-text-faint, #898F9E); }
                footer a { color: inherit; }
              </style>
            </head>
            <body>
            {{bodyHtml}}
              <footer>
                <a href="/">← Back to {{product}}</a>
              </footer>
            </body>
            </html>
            """;
    }
}
