using System.Collections.Concurrent;
using Alcifra.ApiClient;
using Alcifra.Theming;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The palette a server wears (#2319): its anonymous <c>theme</c> rel (ADR 0578), the brand's colours or the variant its
/// operator selected, with any custom theme laid over them. A server profile that chose no style follows it, read when the
/// server is chosen, so the logon window shows it too (owner, 2026-11-11: the server's palette by default, a person may
/// still pick another style).
/// </summary>
/// <remarks>
/// Read through the shared <see cref="ServerTheme"/> (ADR 0917), held per server for the session, and laid through the
/// same contrast rules as any custom theme. A server that cannot be reached, or an older one without the rel, leaves
/// the shipped design: a colour scheme is never worth an error.
/// </remarks>
internal static class ServerPalette
{
    private static readonly ServerTheme Reader = new();
    private static readonly ConcurrentDictionary<string, ThemeTokensReader.ThemeLoad> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A palette pinned for the whole process (<c>Branding__Variant</c>, #2319), which only the manual's capture sets. It
    /// stands where the server's palette would, so no server is asked: a capture whose sign-in profile names a live server
    /// would otherwise wear that server's colours, or not, depending on whether it answered before the screenshot.
    /// </summary>
    public static ThemeTokensReader.ThemeLoad? Pinned { get; private set; }

    public static void Pin(ThemeTokensReader.ThemeLoad? load) => Pinned = load;

    /// <summary>
    /// For a headless render (the manual's sign-in and server-manager figures): the brand's own palette, unless a variant is
    /// pinned already, so the figure never depends on whether the server its profile names is reachable.
    /// </summary>
    public static void PinUnlessPinned() => Pinned ??= new ThemeTokensReader.ThemeLoad(ThemeTokensReader.Of(null), true, []);

    /// <summary>Whether a profile's stored style means "the server's palette": none chosen, or the default entry.</summary>
    public static bool MeansServer(string? styleId) => string.IsNullOrWhiteSpace(styleId) || styleId == ThemeCatalog.DefaultId;

    /// <summary>Whether that palette is read from the server: it means the server's, and none is pinned.</summary>
    public static bool Follows(string? styleId) => Pinned is null && MeansServer(styleId);

    /// <summary>The server's palette, or null where it cannot be read (ADR 0917's reader, shared with the phone).</summary>
    /// <remarks>
    /// The fetch and the parse live in <see cref="ServerTheme"/> so the two clients cannot drift into their own idea of
    /// what a server said: the phone reads the same rela without this cache, this pinned-variant branch or Avalonia.
    /// </remarks>
    public static async Task<ThemeTokensReader.ThemeLoad?> ReadAsync(string apiRootUrl, CancellationToken cancellationToken = default)
    {
        if (Known.TryGetValue(apiRootUrl, out var known))
        {
            return known;
        }

        if (await Reader.ReadAsync(apiRootUrl, cancellationToken) is not { } tokens)
        {
            return null;
        }

        var load = new ThemeTokensReader.ThemeLoad(tokens, true, []);
        Known[apiRootUrl] = load;
        return load;
    }
}
