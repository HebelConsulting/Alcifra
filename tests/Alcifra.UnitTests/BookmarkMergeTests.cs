using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Bookmarks;
using Alcifra.BrowserHost;

namespace Alcifra.UnitTests;

// ADR 1062: the file format, the three-way merge by bookmark id, and the save that merges again when somebody got there first.
public class BookmarkMergeTests
{
    private static BookmarkNode Mark(string id, string title = "Title", string url = "https://example.test/") => new(id, title, url);

    private static BookmarkNode Folder(string id, string title, params BookmarkNode[] children) => new(id, title, null, children);

    private static string Ids(IReadOnlyList<BookmarkNode> tree) => string.Join(",", tree.Select(n => n.Id));

    [Fact]
    public void A_written_tree_reads_back_unchanged_whatever_its_titles_hold()
    {
        IReadOnlyList<BookmarkNode> tree =
        [
            Mark("a", "Fish & \"chips\" <b>", "https://example.test/?a=1&b=<2>"),
            Folder("f", "Tööls", Mark("b", "Ünder"), Folder("g", "Empty")),
        ];

        Assert.True(BookmarkTree.Same(tree, BookmarkHtml.Parse(BookmarkHtml.Write(tree))));
    }

    [Fact]
    public void A_browsers_own_export_reads_and_gets_the_same_ids_every_time()
    {
        const string Export = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <TITLE>Bookmarks</TITLE>
            <H1>Bookmarks</H1>
            <DL><p>
                <DT><H3 ADD_DATE="1" PERSONAL_TOOLBAR_FOLDER="true">Bar</H3>
                <DL><p>
                    <DT><a href='https://one.test/' ADD_DATE="1">One</a>
                    <DT><A HREF="https://one.test/">One</A>
                </DL><p>
                <DT><A HREF="https://two.test/?x=1&amp;y=2">Two &amp; more</A>
                <DD>A description nobody syncs
            </DL><p>
            """;

        var tree = BookmarkHtml.Parse(Export);

        Assert.Equal(["Bar", "Two & more"], tree.Select(n => n.Title));
        Assert.Equal("https://two.test/?x=1&y=2", tree[1].Url);
        var bar = tree[0].Children!;
        Assert.Equal(2, bar.Count);
        Assert.NotEqual(bar[0].Id, bar[1].Id);
        Assert.True(BookmarkTree.Same(tree, BookmarkHtml.Parse(Export)));
    }

    [Fact]
    public void An_id_written_twice_counts_once_and_nesting_past_the_limit_is_flattened()
    {
        var twice = BookmarkHtml.Parse("""<DL><DT><A HREF="https://a.test/" ID="x">A</A><DT><A HREF="https://b.test/" ID="x">B</A></DL>""");
        Assert.NotEqual(twice[0].Id, twice[1].Id);

        var deep = string.Concat(Enumerable.Repeat("<DL><DT><H3>F</H3>", 100_000));
        var depth = 0;
        for (var level = BookmarkHtml.Parse(deep); level.Count > 0; level = level[0].Children ?? [])
        {
            depth++;
        }

        Assert.InRange(depth, 1, BookmarkHtml.MaxDepth + 1);
    }

    [Fact]
    public void A_change_on_either_side_stands()
    {
        IReadOnlyList<BookmarkNode> @base = [Mark("a", "A"), Mark("b", "B"), Mark("c", "C")];
        IReadOnlyList<BookmarkNode> local = [Mark("a", "A here"), Mark("b", "B"), Mark("c", "C"), Mark("new-here")];
        IReadOnlyList<BookmarkNode> remote = [Mark("a", "A"), Mark("b", "B there"), Mark("new-there"), Mark("c", "C")];

        var merged = BookmarkMerge.Merge(@base, local, remote);

        Assert.Equal(0, merged.Conflicts);
        Assert.Equal("a,b,new-there,c,new-here", Ids(merged.Tree));
        Assert.Equal("A here", merged.Tree[0].Title);
        Assert.Equal("B there", merged.Tree[1].Title);
    }

    [Fact]
    public void A_deletion_wins_unless_the_other_side_edited_or_moved_it()
    {
        IReadOnlyList<BookmarkNode> @base = [Mark("deleted-here", "D"), Mark("deleted-there", "E"), Mark("edited-there", "F"), Mark("edited-here", "G"), Folder("f", "F"), Mark("moved-there", "H")];
        IReadOnlyList<BookmarkNode> local = [Mark("deleted-there", "E"), Mark("edited-here", "G2"), Folder("f", "F"), Mark("moved-there", "H")];
        IReadOnlyList<BookmarkNode> remote = [Mark("deleted-here", "D"), Mark("edited-there", "F2"), Folder("f", "F", Mark("moved-there", "H"))];

        var merged = BookmarkMerge.Merge(@base, local, remote).Tree;

        Assert.DoesNotContain(merged, n => n.Id is "deleted-here" or "deleted-there");
        Assert.Contains(merged, n => n is { Id: "edited-there", Title: "F2" });
        Assert.Contains(merged, n => n is { Id: "edited-here", Title: "G2" });

        var movedThenDeleted = BookmarkMerge.Merge(@base, [Folder("f", "F")], remote).Tree;
        Assert.Contains(movedThenDeleted.Single(n => n.Id == "f").Children!, n => n.Id == "moved-there");
    }

    [Fact]
    public void Bookmarks_edited_on_both_sides_keep_the_browsers_edit_and_the_other_in_conflicts()
    {
        IReadOnlyList<BookmarkNode> @base = [Mark("a", "A")];

        var merged = BookmarkMerge.Merge(@base, [Mark("a", "A here")], [Mark("a", "A there", "https://there.test/")]);

        Assert.Equal(1, merged.Conflicts);
        Assert.Equal("A here", merged.Tree.Single(n => n.Id == "a").Title);
        var conflicts = merged.Tree.Single(n => n.Title == BookmarkMerge.ConflictsFolderName).Children!;
        var loser = Assert.Single(conflicts);
        Assert.Equal(("A there", "https://there.test/"), (loser.Title, loser.Url));
        Assert.NotEqual("a", loser.Id);

        // The same edit from both sides is no conflict.
        Assert.Equal(0, BookmarkMerge.Merge(@base, [Mark("a", "Same")], [Mark("a", "Same")]).Conflicts);

        // Merging again over the first outcome keeps one copy of the loser.
        var again = BookmarkMerge.Merge(@base, [Mark("a", "A here")], [.. merged.Tree.Where(n => n.Id != "a"), Mark("a", "A there", "https://there.test/")]);
        Assert.Single(again.Tree.Single(n => n.Title == BookmarkMerge.ConflictsFolderName).Children!);
    }

    [Fact]
    public void A_folder_deleted_here_survives_with_what_was_added_to_it_there()
    {
        IReadOnlyList<BookmarkNode> @base = [Folder("f", "F", Mark("a"))];

        var merged = BookmarkMerge.Merge(@base, [], [Folder("f", "F", Mark("a"), Mark("new"))]).Tree;

        Assert.Equal("new", Assert.Single(Assert.Single(merged).Children!).Id);
    }

    [Fact]
    public void Two_folders_moved_into_each_other_both_survive()
    {
        IReadOnlyList<BookmarkNode> @base = [Folder("a", "A"), Folder("b", "B")];

        var merged = BookmarkMerge.Merge(@base, [Folder("b", "B", Folder("a", "A"))], [Folder("a", "A", Folder("b", "B"))]).Tree;

        var ids = new List<string>();
        void Walk(IReadOnlyList<BookmarkNode> nodes)
        {
            foreach (var node in nodes)
            {
                ids.Add(node.Id);
                Walk(node.Children ?? []);
            }
        }

        Walk(merged);
        Assert.Equal(["a", "b"], ids.Order());
    }

    [Fact]
    public void The_order_follows_the_side_that_reordered()
    {
        IReadOnlyList<BookmarkNode> @base = [Mark("a"), Mark("b"), Mark("c")];
        IReadOnlyList<BookmarkNode> reordered = [Mark("c"), Mark("a"), Mark("b")];

        Assert.Equal("c,a,b", Ids(BookmarkMerge.Merge(@base, reordered, @base).Tree));
        Assert.Equal("c,a,b", Ids(BookmarkMerge.Merge(@base, @base, reordered).Tree));
    }

    [Fact]
    public void A_shared_file_holds_web_addresses_only()
    {
        IReadOnlyList<BookmarkNode> tree = [Mark("a", "Good"), Mark("b", "Bad", "javascript:alert(1)"), Folder("f", "F", Mark("c", "Bad", "data:text/html,x"))];

        var clean = BookmarkTree.WebAddressesOnly(tree);

        Assert.Equal("a,f", Ids(clean));
        Assert.Empty(clean[1].Children!);
    }

    private sealed class FakeRemote(string? html, bool canEdit = true, bool isPersonal = true) : IBookmarkRemote
    {
        public string? Html { get; set; } = html;

        public int Tag { get; private set; }

        public Action? BeforeSave { get; set; }

        public int Saves { get; private set; }

        public Task<IReadOnlyList<BookmarkFileInfo>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BookmarkFileInfo>>(
                [new BookmarkFileInfo(FileId, "Bookmarks", "My Documents", isPersonal, canEdit, Html is not null, $"\"{Tag}\"", LinkMap.Empty)]);

        public int Ensured { get; private set; }

        public Task EnsurePersonalAsync(CancellationToken cancellationToken)
        {
            Ensured++;
            return Task.CompletedTask;
        }

        public Task<string> ReadAsync(BookmarkFileInfo file, CancellationToken cancellationToken) => Task.FromResult(Html!);

        public Task SaveAsync(BookmarkFileInfo file, string html, CancellationToken cancellationToken)
        {
            var before = BeforeSave;
            BeforeSave = null;
            before?.Invoke();
            if (file.ETag != $"\"{Tag}\"")
            {
                throw new BookmarkFileChangedException(file.Name);
            }

            Html = html;
            Tag++;
            Saves++;
            return Task.CompletedTask;
        }

        public void SaveBySomebodyElse(IReadOnlyList<BookmarkNode> tree)
        {
            Html = BookmarkHtml.Write(tree);
            Tag++;
        }
    }

    private static readonly Guid FileId = Guid.Parse("2d8b1a50-61d4-4b8e-9b0e-0f5f1f0a7e11");

    [Fact]
    public async Task A_save_that_lost_the_race_reads_again_and_keeps_both_changes()
    {
        IReadOnlyList<BookmarkNode> @base = [Mark("a")];
        var remote = new FakeRemote(BookmarkHtml.Write(@base));
        remote.BeforeSave = () => remote.SaveBySomebodyElse([Mark("a"), Mark("theirs")]);

        var outcome = await BookmarkSync.RunAsync(remote, FileId, @base, [Mark("a"), Mark("mine")], default);

        Assert.True(outcome.Saved);
        Assert.Equal(1, remote.Saves);
        var stored = BookmarkHtml.Parse(remote.Html!);
        Assert.Contains(stored, n => n.Id == "mine");
        Assert.Contains(stored, n => n.Id == "theirs");
    }

    [Fact]
    public async Task Nothing_changed_writes_nothing()
    {
        IReadOnlyList<BookmarkNode> tree = [Mark("a")];
        var remote = new FakeRemote(BookmarkHtml.Write(tree));

        var outcome = await BookmarkSync.RunAsync(remote, FileId, tree, tree, default);

        Assert.False(outcome.Saved);
        Assert.Equal(0, remote.Saves);
    }

    [Fact]
    public async Task A_file_the_caller_may_not_write_comes_back_as_the_server_holds_it()
    {
        IReadOnlyList<BookmarkNode> tree = [Mark("a")];
        var remote = new FakeRemote(BookmarkHtml.Write(tree), canEdit: false, isPersonal: false);

        var outcome = await BookmarkSync.RunAsync(remote, FileId, tree, [Mark("a"), Mark("mine")], default);

        Assert.False(outcome.Saved);
        Assert.Equal("a", Ids(outcome.Tree));
    }

    [Fact]
    public async Task A_shared_file_loses_a_script_address_and_a_personal_one_keeps_it()
    {
        IReadOnlyList<BookmarkNode> tree = [Mark("a"), Mark("script", "Run", "javascript:alert(1)")];

        var shared = await BookmarkSync.RunAsync(new FakeRemote(BookmarkHtml.Write(tree), isPersonal: false), FileId, null, null, default);
        Assert.Equal("a", Ids(shared.Tree));

        var personal = await BookmarkSync.RunAsync(new FakeRemote(BookmarkHtml.Write(tree)), FileId, null, null, default);
        Assert.Equal("a,script", Ids(personal.Tree));
    }

    [Fact]
    public async Task The_first_sync_of_an_empty_file_writes_it_without_a_tag_to_carry()
    {
        var remote = new FakeRemote(null);

        var outcome = await BookmarkSync.RunAsync(remote, FileId, null, null, default);

        Assert.True(outcome.Saved);
        Assert.Empty(BookmarkHtml.Parse(remote.Html!));
    }

    [Fact]
    public async Task A_personal_file_is_made_only_when_there_is_none()
    {
        var hasOne = new FakeRemote(null);
        await BookmarkSync.FilesAsync(hasOne, default);
        Assert.Equal(0, hasOne.Ensured);

        var hasNone = new FakeRemote(null, isPersonal: false);
        await BookmarkSync.FilesAsync(hasNone, default);
        Assert.Equal(1, hasNone.Ensured);
    }

    [Fact]
    public async Task An_answer_or_a_request_past_one_frame_travels_in_chunks()
    {
        var large = new string('x', 3 * NativeMessaging.MaxBytes);
        using var wire = new MemoryStream();
        await NativeMessaging.WriteChunkedAsync(wire, 7, new { id = 7L, value = large });

        wire.Position = 0;
        var assembly = new NativeMessaging.Assembly();
        JsonDocument? whole = null;
        var frames = 0;
        while (await NativeMessaging.ReadAsync(wire) is { } frame)
        {
            using (frame)
            {
                frames++;
                Assert.True(NativeMessaging.IsChunk(frame));
                Assert.Equal(7, frame.RootElement.GetProperty("id").GetInt64());
                whole = assembly.Add(frame);
            }
        }

        Assert.True(frames > 1);
        using (whole)
        {
            Assert.Equal(large, whole!.RootElement.GetProperty("value").GetString());
        }
    }
}
