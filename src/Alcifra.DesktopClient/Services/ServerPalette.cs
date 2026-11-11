using System.Collections.Concurrent;
using System.Text.Json;
using Alcifra.Theming;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The palette a server wears (#2319): its anonymous <c>theme</c> rel (ADR 0578), the brand's colours or the variant its
/// operator selected, with any custom theme laid over them. A server profile that chose no style follows it, read when the
/// server is chosen, so the logon window shows it too (owner, 2026-11-11: the server's palette by default, a person may
/// still pick another style).
/// </summary>
/// <remarks>
/// Followed from the API root rather than composed, held per server for the session, and laid through the same contrast
/// rules as any custom theme. A server that cannot be reached, or an older one without the rel, leaves the shipped design:
/// a colour scheme is never worth an error.
/// </remarks>
internal static class ServerPalette
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, ThemeTokensReader.ThemeLoad> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a profile's stored style means "the server's palette": none chosen, or the default entry.</summary>
    public static bool Follows(string? styleId) => string.IsNullOrWhiteSpace(styleId) || styleId == ThemeCatalog.DefaultId;

    /// <summary>The server's palette, or null where it cannot be read or adds nothing to the shipped design.</summary>
    public static async Task<ThemeTokensReader.ThemeLoad?> ReadAsync(string apiRootUrl, CancellationToken cancellationToken = default)
    {
        if (Known.TryGetValue(apiRootUrl, out var known))
        {
            return known;
        }

        try
        {
            var root = new Uri(apiRootUrl.TrimEnd('/') + "/");
            using var api = JsonDocument.Parse(await Http.GetStringAsync(new Uri(root, "api"), cancellationToken));
            var href = api.RootElement.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
                ? links.EnumerateArray().Where(l => l.TryGetProperty("rel", out var rel) && rel.GetString() == "theme")
                    .Select(l => l.GetProperty("href").GetString()).FirstOrDefault()
                : null;
            if (href is null)
            {
                return null;
            }

            var load = ThemeTokensReader.Load(await Http.GetStringAsync(new Uri(root, href.TrimStart('/')), cancellationToken));
            if (!load.Applied)
            {
                return null;
            }

            Known[apiRootUrl] = load;
            return load;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException or KeyNotFoundException)
        {
            DesktopLog.Debug("The server's palette could not be read from {Server}: {Reason}", apiRootUrl, e.Message);
            return null;
        }
    }
}
