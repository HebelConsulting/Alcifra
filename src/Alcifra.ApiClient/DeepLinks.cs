using System;
using Alcifra.Presentation;

namespace Alcifra.ApiClient;

/// <summary>
/// The deep-link address forms (#761), shared by the desktop and the phone (ADR 0917): what "Copy link" produces and what "Go to link…" / the
/// <c>alcifra://</c> scheme handler accept. Pure, so every accepted shape is pinned in a unit test.
/// </summary>
/// <remarks>
/// What CIRCULATES is always the https web-app URL (<c>{server}/go/{id}</c>) — universally openable by any
/// recipient in a browser, whichever client the sender used. The <c>alcifra://go/{id}</c> scheme form is
/// additionally accepted (and registered with the OS) so a link rewritten to it opens the desktop client
/// natively; the parser takes both, because a paste box that rejects the app's own scheme would be absurd.
/// </remarks>
public static class DeepLinks
{
    public const string Scheme = WireNames.DeepLinkScheme;

    /// <summary>
    /// Every scheme a link may arrive on: the current one, and the one before the rename, which the clients still
    /// register and accept until <see cref="WireNames.Legacy"/> retires (ADR 1001).
    /// </summary>
    public static readonly string[] Schemes = [Scheme, WireNames.Legacy.DeepLinkScheme];

    /// <summary>Whether <paramref name="text"/> is a link on one of <see cref="Schemes"/>.</summary>
    public static bool IsSchemeLink(string text) =>
        Array.Exists(Schemes, s => text.StartsWith($"{s}://", StringComparison.OrdinalIgnoreCase));

    /// <summary>The link "Copy link" puts on the clipboard: the web app's /go route on this client's server.</summary>
    public static string BuildLink(string apiBaseUrl, Guid documentId) =>
        $"{apiBaseUrl.TrimEnd('/')}/go/{documentId}";

    /// <summary>The document id carried by a pasted or scheme-launched link, or null when the text is neither
    /// form. Accepts <c>http(s)://…/go/{id}</c> and <c>alcifra://go/{id}</c> (host or first segment), on either scheme.</summary>
    public static Guid? ParseDocumentId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // alcifra://go/{id} parses with Host "go" and the id as the path; a tolerant variant with an
        // extra slash (alcifra:///go/{id}) carries both in the path instead. Normalise to segments.
        var isScheme = Array.IndexOf(Schemes, uri.Scheme) >= 0;
        var segments = ((uri.Host is { Length: > 0 } host && !host.Contains('.') && isScheme ? host + uri.AbsolutePath : uri.AbsolutePath))
            .Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var isWeb = uri.Scheme is "http" or "https";
        if (!isWeb && !isScheme)
        {
            return null;
        }

        return segments.Length >= 2 && segments[^2].Equals("go", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[^1], out var id)
            ? id
            : null;
    }
}
