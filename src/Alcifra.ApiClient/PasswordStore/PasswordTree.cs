using System.Security.Cryptography;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>
/// One row of a store's whole tree, as a client holds it: the flat listing's values plus the PATH a person types
/// (<c>Mail/Banking/login</c>), built from the parent ids in the set.
/// </summary>
public sealed record PasswordTreeEntry(
    Guid Id, string Name, bool IsFolder, Guid ParentId, string Href, string Path,
    string? Site = null, string? UserName = null, string? App = null);

/// <summary>
/// The tree model BOTH clients share (apass's commands and the desktop's browser host): turning the flat listing
/// into paths, listing a folder's children, and normalising what a user typed. One implementation, because two
/// copies of a path walk drift on exactly the edge cases — a missing ancestor, a cycle — that nobody tests twice.
/// </summary>
public static class PasswordTree
{
    /// <summary>Paths for a flat listing: each row's ancestors' names, joined by '/', below the store itself.</summary>
    public static IReadOnlyList<PasswordTreeEntry> WithPaths(Guid storeId, IReadOnlyList<PasswordDescendantInfo> flat)
    {
        var byId = flat.ToDictionary(i => i.Id);
        return [.. flat.Select(row => new PasswordTreeEntry(
            row.Id, row.Name, row.IsFolder, row.ParentId, row.Href, PathOf(row), row.Site, row.UserName, row.App))];

        string PathOf(PasswordDescendantInfo row)
        {
            var names = new List<string>();
            var current = row;
            var seen = new HashSet<Guid>();
            // A cycle or an ancestor missing from the set ends the walk: the row keeps the names it could prove
            // rather than hanging or vanishing.
            while (current.Id != storeId && seen.Add(current.Id))
            {
                names.Add(current.Name);
                if (current.ParentId == storeId || !byId.TryGetValue(current.ParentId, out var parent))
                {
                    break;
                }

                current = parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }
    }

    /// <summary>The row at <paramref name="path"/>, or null.</summary>
    public static PasswordTreeEntry? FindOrNull(IReadOnlyList<PasswordTreeEntry> rows, string path) =>
        rows.FirstOrDefault(r => string.Equals(r.Path, Normalise(path), StringComparison.Ordinal));

    /// <summary>The direct children of a path (empty means the store's root), folders first, then by name.</summary>
    public static IReadOnlyList<PasswordTreeEntry> Children(IReadOnlyList<PasswordTreeEntry> rows, string path)
    {
        var parent = Normalise(path);
        return [.. rows
            .Where(r => ParentOf(r.Path) == parent)
            .OrderBy(r => r.IsFolder ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The path without its last segment; empty for a top-level row.</summary>
    public static string ParentOf(string path) =>
        path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;

    /// <summary>A path as typed, without surrounding or trailing slashes.</summary>
    public static string Normalise(string? path) => (path ?? string.Empty).Trim().Trim('/');
}
