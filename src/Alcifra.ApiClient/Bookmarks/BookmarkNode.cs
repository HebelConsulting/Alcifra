using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Alcifra.ApiClient.Bookmarks;

/// <summary>One bookmark, or a folder when it has no <paramref name="Url"/>. <paramref name="Id"/> stays with it through
/// every rename and move (ADR 1062), which is what lets two copies of a file be merged bookmark by bookmark.</summary>
public sealed record BookmarkNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("children"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BookmarkNode>? Children = null)
{
    [JsonIgnore]
    public bool IsFolder => Url is null;
}

/// <summary>What is true of a whole bookmark tree, whichever side it came from.</summary>
public static class BookmarkTree
{
    /// <summary>Whether two trees hold the same bookmarks in the same places and the same order.</summary>
    public static bool Same(IReadOnlyList<BookmarkNode> a, IReadOnlyList<BookmarkNode> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Title != b[i].Title || a[i].Url != b[i].Url
                || !Same(a[i].Children ?? [], b[i].Children ?? []))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The tree without what is not a web address. A shared file is written by colleagues and clicked on whatever page is
    /// open, so a script address in one would run there as the person who clicked it (ADR 1062). A person's own file may
    /// hold their own bookmarklets.
    /// </summary>
    public static IReadOnlyList<BookmarkNode> WebAddressesOnly(IReadOnlyList<BookmarkNode> tree) =>
    [
        .. tree
            .Where(n => n.IsFolder || IsWebAddress(n.Url!))
            .Select(n => n.IsFolder ? n with { Children = WebAddressesOnly(n.Children ?? []) } : n),
    ];

    /// <summary>An id that comes out the same every time for the same inputs, for what carries none of its own.</summary>
    internal static string DerivedId(params string?[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', parts))).AsSpan(0, 16));

    private static bool IsWebAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
