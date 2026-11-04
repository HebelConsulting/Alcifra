namespace Alcifra.Presentation;

/// <summary>What a client knows about a preview before fetching any of it, and all it needs to decide whether to fetch.</summary>
public sealed record PreviewFacts(long? SizeBytes, string? FileExtension, string? PosterHref = null);

/// <summary>
/// When a document version's preview waits for a click on Open instead of loading by itself (ADR 1048). Both clients ask
/// here. The limit is counted the way both clients show a size, so a file shown as 10 MB does not wait.
/// </summary>
public static class LargePreview
{
    public const long LimitBytes = 10L * 1024 * 1024;

    /// <summary>An unknown size loads, as it always has: nothing says it is large.</summary>
    public static bool Waits(long? sizeBytes) => sizeBytes > LimitBytes;
}

/// <summary>
/// Video and sound by extension (#2160). A preview never fetches such a file to look at it: no client can draw its bytes,
/// and the size says nothing about how far anyone wants to watch. Known by the name, since sniffing means fetching.
/// </summary>
public static class MediaFiles
{
    private static readonly HashSet<string> BrowserVideo = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov", "webm", "ogv" };
    private static readonly HashSet<string> BrowserSound = new(StringComparer.OrdinalIgnoreCase) { "mp3", "m4a", "aac", "wav", "ogg", "oga", "opus", "flac" };
    private static readonly HashSet<string> Others = new(StringComparer.OrdinalIgnoreCase)
    {
        "avi", "mkv", "wmv", "flv", "mpg", "mpeg", "m2ts", "mts", "ts", "3gp", "vob", "wma", "aif", "aiff", "mid", "midi",
    };

    public static bool IsMedia(string? extension) =>
        IsVideoABrowserPlays(extension) || IsSoundABrowserPlays(extension) || Others.Contains(Bare(extension));

    /// <summary>The reference field whose document is a video's picture before it plays (#2160, ADR 1053).</summary>
    public const string ThumbnailField = "Thumbnail";

    public static bool IsVideoABrowserPlays(string? extension) => BrowserVideo.Contains(Bare(extension));

    public static bool IsSoundABrowserPlays(string? extension) => BrowserSound.Contains(Bare(extension));

    private static string Bare(string? extension) => (extension ?? string.Empty).TrimStart('.');
}
