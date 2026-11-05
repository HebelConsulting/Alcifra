using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcifra.ApiClient.Bookmarks;

/// <summary>
/// The bookmark HTML format every browser imports and exports (ADR 1062), with each bookmark's stable id in an <c>ID</c>
/// attribute, which browsers ignore on import.
/// </summary>
/// <remarks>
/// A file may come from anywhere: downloaded, edited by hand, exported by another browser and uploaded. So reading is
/// forgiving of the format's unclosed tags, and something without an id gets one derived from where it stands, the same
/// on every read until a save writes it down.
/// </remarks>
public static partial class BookmarkHtml
{
    /// <summary>How deep folders nest here; deeper ones are flattened. A file is someone else's input, and everything that
    /// walks the tree recurses.</summary>
    public const int MaxDepth = 24;

    [GeneratedRegex("""<(/?)(dl|h3|a)\b((?:"[^"]*"|'[^']*'|[^'">])*)>""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Tag();

    [GeneratedRegex("""\b(id|href)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Attribute();

    public static IReadOnlyList<BookmarkNode> Parse(string html)
    {
        var root = new Draft(string.Empty, string.Empty, null);
        var open = new Stack<Draft>([root]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        Draft? lastFolder = null;
        (bool IsFolder, string Attributes, int TextStart)? heading = null;

        foreach (Match tag in Tag().Matches(html))
        {
            var closing = tag.Groups[1].Length > 0;
            var name = tag.Groups[2].Value.ToLowerInvariant();
            if (name == "dl")
            {
                if (closing)
                {
                    if (open.Count > 1)
                    {
                        open.Pop();
                    }
                }
                else
                {
                    // Every open list is pushed, the root's and a flattened folder's too, so each close has its open.
                    open.Push(lastFolder is not null && open.Count <= MaxDepth ? lastFolder : open.Peek());
                    lastFolder = null;
                }
            }
            else if (!closing)
            {
                heading = (name == "h3", tag.Groups[3].Value, tag.Index + tag.Length);
                lastFolder = null;
            }
            else if (heading is { } opened && opened.IsFolder == (name == "h3"))
            {
                heading = null;
                var title = WebUtility.HtmlDecode(html[opened.TextStart..tag.Index]).Trim();
                var (id, href) = ReadAttributes(opened.Attributes);
                if (!opened.IsFolder && string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var parent = open.Peek();
                var draft = new Draft(UniqueId(id, parent, title, opened.IsFolder ? null : href, seen, occurrences), title, opened.IsFolder ? null : href);
                parent.Children.Add(draft);
                lastFolder = opened.IsFolder ? draft : null;
            }
        }

        return root.Freeze().Children ?? [];
    }

    public static string Write(IReadOnlyList<BookmarkNode> tree)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n")
            .Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n")
            .Append("<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n");
        WriteList(html, tree, 0);
        return html.ToString();
    }

    private static void WriteList(StringBuilder html, IReadOnlyList<BookmarkNode> nodes, int depth)
    {
        var indent = new string(' ', depth * 4);
        html.Append(indent).Append("<DL><p>\n");
        foreach (var node in nodes)
        {
            html.Append(indent).Append("    <DT>");
            if (node.IsFolder)
            {
                html.Append($"<H3 ID=\"{Encode(node.Id)}\">{Encode(node.Title)}</H3>\n");
                WriteList(html, node.Children ?? [], depth + 1);
            }
            else
            {
                html.Append($"<A HREF=\"{Encode(node.Url!)}\" ID=\"{Encode(node.Id)}\">{Encode(node.Title)}</A>\n");
            }
        }

        html.Append(indent).Append("</DL><p>\n");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static (string? Id, string? Href) ReadAttributes(string attributes)
    {
        string? id = null, href = null;
        foreach (Match attribute in Attribute().Matches(attributes))
        {
            var value = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Value);
            if (attribute.Groups[1].Value.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                id = value;
            }
            else
            {
                href = value;
            }
        }

        return (id, href);
    }

    // An id written twice, by a hand or a foreign tool, is honoured once: two bookmarks cannot be one.
    private static string UniqueId(string? written, Draft parent, string title, string? url, HashSet<string> seen, Dictionary<string, int> occurrences)
    {
        if (!string.IsNullOrEmpty(written) && seen.Add(written))
        {
            return written;
        }

        var place = BookmarkTree.DerivedId(parent.Id, title, url);
        var occurrence = occurrences[place] = occurrences.GetValueOrDefault(place) + 1;
        var derived = BookmarkTree.DerivedId(place, occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        seen.Add(derived);
        return derived;
    }

    private sealed class Draft(string id, string title, string? url)
    {
        public string Id => id;

        public List<Draft> Children { get; } = [];

        public BookmarkNode Freeze() => new(id, title, url, url is null ? [.. Children.Select(c => c.Freeze())] : null);
    }
}
