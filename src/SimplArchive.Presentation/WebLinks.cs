namespace SimplArchive.Presentation;

/// <summary>
/// Whether a value opens as a web page, and where (core #1949): only http and https, so a link never runs a script,
/// opens a file or starts another app. A password entry's URL is written by anyone who may write to its store. One
/// rule for both native clients, for an entry's URL and an index field's.
/// </summary>
public static class WebLinks
{
    /// <summary>An absolute http(s) address, or null (an index field's URL, validated as absolute, ADR 0755).</summary>
    public static Uri? Absolute(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && IsWeb(uri) ? uri : null;

    /// <summary>
    /// A URL as people type it (owner decision, #1949): an absolute http(s) address, or a host with an optional port and
    /// path (<c>bank.example</c>, <c>www.bank.example/login</c>, <c>localhost:8080</c>) opened as https. Another scheme
    /// (<c>javascript:</c>, <c>mailto:</c>, <c>file://</c>), text with spaces, or a word that is no host gives null.
    /// </summary>
    public static Uri? Typed(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Absolute(text);
        }

        if (HasScheme(text))
        {
            return null;
        }

        return Uri.TryCreate($"https://{text}", UriKind.Absolute, out var uri)
            && (uri.Host.Contains('.', StringComparison.Ordinal) || uri.IsLoopback) ? uri : null;
    }

    private static bool IsWeb(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    // "mailto:…", "javascript:…", "tel:+41…": a scheme of its own, never read as a host. A colon followed by a digit is
    // a host's port ("localhost:8080"), not a scheme.
    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && char.IsAsciiLetter(text[0])
            && text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            && !(colon + 1 < text.Length && char.IsAsciiDigit(text[colon + 1]));
    }
}
