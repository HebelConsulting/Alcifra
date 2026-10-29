using System.Net;
using System.Text.RegularExpressions;

namespace Alcifra.Presentation;

/// <summary>
/// A search hit's highlighted fragment as the clients show it (ADR "Search result highlighting"): the server wraps
/// each matched term in <c>&lt;em&gt;…&lt;/em&gt;</c> and HTML-escapes everything else. Split into runs, each
/// HTML-decoded and marked whether it is a hit, so the desktop and the phone (SimplArchiveMobile #24 found the phone
/// printing the raw tags) render the same thing; how a hit LOOKS is each client's business.
/// </summary>
public static partial class SearchSnippet
{
    [GeneratedRegex("<em>(.*?)</em>", RegexOptions.Singleline)]
    private static partial Regex EmTag();

    /// <summary>The fragment's runs in order; empty for no fragment.</summary>
    public static IReadOnlyList<(string Text, bool IsHit)> Parts(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var parts = new List<(string, bool)>();
        var last = 0;
        foreach (Match match in EmTag().Matches(html))
        {
            if (match.Index > last)
            {
                parts.Add((WebUtility.HtmlDecode(html[last..match.Index]), false));
            }

            parts.Add((WebUtility.HtmlDecode(match.Groups[1].Value), true));
            last = match.Index + match.Length;
        }

        if (last < html.Length)
        {
            parts.Add((WebUtility.HtmlDecode(html[last..]), false));
        }

        return parts;
    }
}
