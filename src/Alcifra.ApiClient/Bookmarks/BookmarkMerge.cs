namespace Alcifra.ApiClient.Bookmarks;

/// <summary>A merged tree, and how many bookmarks were edited on both sides at once.</summary>
public sealed record BookmarkMerged(IReadOnlyList<BookmarkNode> Tree, int Conflicts);

/// <summary>
/// Three copies of a file become one, bookmark by bookmark (ADR 1062): what this browser last agreed with the server
/// (<c>base</c>), what the browser holds now (<c>local</c>) and what the server holds now (<c>remote</c>).
/// </summary>
/// <remarks>
/// <para>A change made on one side stands. A deletion wins unless the other side edited or moved that bookmark. A folder
/// that something survives in survives with it, so nothing added on one side vanishes into a folder deleted on the other.</para>
/// <para>When one bookmark was edited differently on both sides the browser's edit wins, because it is the one arriving
/// after the server's. The other is kept as a new bookmark in a "Conflicts" folder.</para>
/// </remarks>
public static class BookmarkMerge
{
    /// <summary>The name of the folder the losing edits go to.</summary>
    public const string ConflictsFolderName = "Conflicts";

    private const string ConflictsFolderId = "conflicts";
    private static readonly string Root = string.Empty;

    public static BookmarkMerged Merge(IReadOnlyList<BookmarkNode>? @base, IReadOnlyList<BookmarkNode>? local, IReadOnlyList<BookmarkNode> remote)
    {
        // No browser copy yet: nothing can have changed there.
        if (local is null)
        {
            return new BookmarkMerged(remote, 0);
        }

        var b = new Flat(@base ?? []);
        var l = new Flat(local);
        var r = new Flat(remote);

        var kept = new Dictionary<string, Kept>(StringComparer.Ordinal);
        var losers = new List<BookmarkNode>();
        foreach (var id in l.Places.Keys.Concat(r.Places.Keys).Distinct(StringComparer.Ordinal))
        {
            b.Places.TryGetValue(id, out var was);
            l.Places.TryGetValue(id, out var here);
            r.Places.TryGetValue(id, out var there);
            if (Decide(was, here, there, losers) is { } decided)
            {
                kept[id] = decided;
            }
        }

        KeepFoldersThatStillHoldSomething(kept, l, r);
        BreakCycles(kept);
        var conflicts = AddLosers(kept, losers);

        var byParent = kept.GroupBy(k => k.Value.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(k => k.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return new BookmarkMerged(Build(Root, kept, byParent, b, l, r), conflicts);
    }

    private static Kept? Decide(Place? was, Place? here, Place? there, List<BookmarkNode> losers)
    {
        if (here is not null && there is not null)
        {
            if (!SameContent(here.Node, there.Node) && was is not null && !SameContent(was.Node, here.Node) && !SameContent(was.Node, there.Node)
                && !there.Node.IsFolder)
            {
                losers.Add(there.Node);
            }

            var content = was is not null && SameContent(was.Node, here.Node) ? there.Node : here.Node;
            var movedHere = was is not null && was.Parent != here.Parent;
            var movedThere = was is not null && was.Parent != there.Parent;
            return new Kept(content, movedHere || !movedThere ? here.Parent : there.Parent);
        }

        // On one side only: new there, or deleted on the other side, where the deletion stands unless this side changed it.
        var survivor = (here ?? there)!;
        return was is null || !SameContent(was.Node, survivor.Node) || was.Parent != survivor.Parent
            ? new Kept(survivor.Node, survivor.Parent)
            : null;
    }

    private static void KeepFoldersThatStillHoldSomething(Dictionary<string, Kept> kept, Flat local, Flat remote)
    {
        var walking = new Queue<string>(kept.Keys);
        while (walking.TryDequeue(out var id))
        {
            var parent = kept[id].Parent;
            if (parent == Root || kept.ContainsKey(parent))
            {
                continue;
            }

            if ((local.Places.GetValueOrDefault(parent) ?? remote.Places.GetValueOrDefault(parent)) is { } folder)
            {
                kept[parent] = new Kept(folder.Node, folder.Parent);
                walking.Enqueue(parent);
            }
            else
            {
                kept[id] = kept[id] with { Parent = Root };
            }
        }
    }

    // A moved under B here while B moved under A there: neither is reachable, and both are somebody's bookmarks.
    private static void BreakCycles(Dictionary<string, Kept> kept)
    {
        foreach (var id in kept.Keys.ToList())
        {
            var path = new HashSet<string>(StringComparer.Ordinal) { id };
            for (var at = kept[id].Parent; at != Root; at = kept[at].Parent)
            {
                if (!path.Add(at))
                {
                    kept[at] = kept[at] with { Parent = Root };
                    break;
                }
            }
        }
    }

    private static int AddLosers(Dictionary<string, Kept> kept, List<BookmarkNode> losers)
    {
        if (losers.Count == 0)
        {
            return 0;
        }

        if (!kept.ContainsKey(ConflictsFolderId))
        {
            kept[ConflictsFolderId] = new Kept(new BookmarkNode(ConflictsFolderId, ConflictsFolderName, null, []), Root);
        }

        foreach (var loser in losers)
        {
            // Derived from what lost, so a save that is retried does not keep it twice.
            var id = BookmarkTree.DerivedId(loser.Id, loser.Title, loser.Url);
            kept.TryAdd(id, new Kept(loser with { Id = id }, ConflictsFolderId));
        }

        return losers.Count;
    }

    private static List<BookmarkNode> Build(string parent, Dictionary<string, Kept> kept, Dictionary<string, HashSet<string>> byParent, Flat b, Flat l, Flat r)
    {
        if (!byParent.TryGetValue(parent, out var members))
        {
            return [];
        }

        return
        [
            .. Order(parent, members, b, l, r).Select(id =>
            {
                var node = kept[id].Node;
                return node.IsFolder ? node with { Children = Build(id, kept, byParent, b, l, r) } : node with { Children = null };
            }),
        ];
    }

    // The order of the side that reordered, the browser's when both did, with what only the other side has placed after
    // the neighbour it follows there.
    private static List<string> Order(string parent, HashSet<string> members, Flat b, Flat l, Flat r)
    {
        var was = b.Children(parent);
        var here = l.Children(parent);
        var there = r.Children(parent);
        var (lead, other) = Reordered(was, here) ? (here, there) : (there, here);

        var order = lead.Where(members.Contains).ToList();
        var placed = order.ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        var previousAt = -1;
        foreach (var id in other.Where(members.Contains))
        {
            if (placed.Add(id))
            {
                previousAt = previous is null ? 0 : (previousAt >= 0 && previousAt < order.Count && order[previousAt] == previous ? previousAt : order.IndexOf(previous)) + 1;
                order.Insert(previousAt, id);
            }
            else
            {
                previousAt = -1;
            }

            previous = id;
        }

        order.AddRange(members.Where(placed.Add).Order(StringComparer.Ordinal));
        return order;
    }

    private static bool Reordered(IReadOnlyList<string> was, IReadOnlyList<string> now)
    {
        var inBoth = now.ToHashSet(StringComparer.Ordinal);
        var before = was.Where(inBoth.Contains).ToList();
        inBoth = [.. was];
        return !before.SequenceEqual(now.Where(inBoth.Contains));
    }

    private static bool SameContent(BookmarkNode a, BookmarkNode b) => a.Title == b.Title && a.Url == b.Url;

    private sealed record Place(BookmarkNode Node, string Parent);

    private sealed record Kept(BookmarkNode Node, string Parent);

    private sealed class Flat
    {
        private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

        public Flat(IReadOnlyList<BookmarkNode> tree) => Add(tree, Root);

        public Dictionary<string, Place> Places { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Children(string parent) => _children.TryGetValue(parent, out var ids) ? ids : [];

        private void Add(IReadOnlyList<BookmarkNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                // The browser's side is input too: an id seen twice counts once.
                if (Places.TryAdd(node.Id, new Place(node, parent)))
                {
                    if (!_children.TryGetValue(parent, out var ids))
                    {
                        _children[parent] = ids = [];
                    }

                    ids.Add(node.Id);
                    Add(node.Children ?? [], node.Id);
                }
            }
        }
    }
}
