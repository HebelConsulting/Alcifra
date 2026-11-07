using System.Text.Json;
using Alcifra.ApiClient.Bookmarks;

namespace Alcifra.BrowserExtensionTests;

/// <summary>
/// The bookmark sync as a browser runs it (ADR 1062): the shipped extension's real bookmarks, through the real host
/// protocol, against a server's files kept on disk. Each test gets its own browser and profile.
/// </summary>
public sealed class BookmarkSyncExtensionTests
{
    private const string Pass = "apass";

    private static readonly FakeBookmarkFile Personal = new(Guid.Parse("7a1f6f7e-59d0-4f0b-8a53-0c3a3f1f2a01"), "Bookmarks", IsPersonal: true, CanEdit: true);
    private static readonly FakeBookmarkFile ReadOnly = new(Guid.Parse("7a1f6f7e-59d0-4f0b-8a53-0c3a3f1f2a02"), "Team links", IsPersonal: false, CanEdit: false);

    private static string Html(params BookmarkNode[] nodes) => BookmarkHtml.Write(nodes);

    private static BookmarkNode Mark(string id, string title, string url = "https://example.test/") => new(id, title, url);

    private static IReadOnlyList<BookmarkNode> Stored(ExtensionBrowser browser, FakeBookmarkFile file) =>
        BookmarkHtml.Parse(browser.Bookmarks.Html(file.Id));

    [Fact]
    public async Task A_file_becomes_its_own_folder_and_a_new_bookmark_goes_back_with_an_id()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(Personal, Html(Mark("a", "Mail"), new BookmarkNode("f", "Tools", null, [Mark("b", "Docs")])));

        await browser.SyncBookmarksAsync();

        var folder = await browser.BrowserFolderAsync("Bookmarks");
        Assert.Equal("Mail", folder[0].GetProperty("title").GetString());
        Assert.Equal("Docs", folder[1].GetProperty("children")[0].GetProperty("title").GetString());

        await browser.BookmarksApiAsync("""
            async () => {
              const [folder] = await chrome.bookmarks.search({ title: "Bookmarks" });
              await chrome.bookmarks.create({ parentId: folder.id, title: "New here", url: "https://new.test/" });
            }
            """);
        await browser.SyncBookmarksAsync();

        var added = Stored(browser, Personal).Single(n => n.Title == "New here");
        Assert.Equal("https://new.test/", added.Url);
        Assert.False(string.IsNullOrEmpty(added.Id));
    }

    [Fact]
    public async Task A_change_on_the_server_reaches_the_browser_and_a_change_made_there_saves_by_itself()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], null, TimeSpan.FromSeconds(2), Pass);
        browser.Bookmarks.Put(Personal, Html(Mark("a", "Mail")));
        await browser.SyncBookmarksAsync();

        browser.Bookmarks.Put(Personal, Html(Mark("a", "Mail renamed"), Mark("b", "Added elsewhere")));
        await browser.SyncBookmarksAsync();
        var folder = await browser.BrowserFolderAsync("Bookmarks");
        Assert.Equal(["Mail renamed", "Added elsewhere"], folder.Select(n => n.GetProperty("title").GetString()));

        await browser.BookmarksApiAsync("""
            async () => {
              const [mark] = await chrome.bookmarks.search({ title: "Added elsewhere" });
              await chrome.bookmarks.remove(mark.id);
            }
            """);

        await WaitUntilAsync(() => Stored(browser, Personal).Count == 1, "the change to be saved after it settled");
        Assert.Equal("a", Stored(browser, Personal)[0].Id);
    }

    [Fact]
    public async Task One_bookmark_edited_in_both_places_keeps_the_browsers_edit_and_files_the_other_in_conflicts()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(Personal, Html(Mark("a", "Mail")));
        await browser.SyncBookmarksAsync();

        browser.Bookmarks.Put(Personal, Html(Mark("a", "Their name")));
        await browser.BookmarksApiAsync("""
            async () => {
              const [mark] = await chrome.bookmarks.search({ title: "Mail" });
              await chrome.bookmarks.update(mark.id, { title: "My name" });
            }
            """);
        await browser.SyncBookmarksAsync();

        var folder = await browser.BrowserFolderAsync("Bookmarks");
        Assert.Equal(["My name", BookmarkMerge.ConflictsFolderName], folder.Select(n => n.GetProperty("title").GetString()));
        Assert.Equal("Their name", folder[1].GetProperty("children")[0].GetProperty("title").GetString());
        Assert.Contains(Stored(browser, Personal), n => n is { Id: "a", Title: "My name" });
    }

    [Fact]
    public async Task A_change_to_a_read_only_file_is_reverted_and_never_written()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(ReadOnly, Html(Mark("a", "Handbook")));
        var before = browser.Bookmarks.Html(ReadOnly.Id);
        await browser.SyncBookmarksAsync();

        await browser.BookmarksApiAsync("""
            async () => {
              const [mark] = await chrome.bookmarks.search({ title: "Handbook" });
              await chrome.bookmarks.update(mark.id, { title: "Mine now" });
              await chrome.bookmarks.create({ parentId: mark.parentId, title: "Added", url: "https://added.test/" });
            }
            """);
        await browser.SyncBookmarksAsync();

        var folder = await browser.BrowserFolderAsync("Team links");
        Assert.Equal(["Handbook"], folder.Select(n => n.GetProperty("title").GetString()));
        Assert.Equal(before, browser.Bookmarks.Html(ReadOnly.Id));
    }

    [Fact]
    public async Task A_folder_removed_by_hand_comes_back_and_the_file_is_not_emptied()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(Personal, Html(Mark("a", "Mail")));
        await browser.SyncBookmarksAsync();

        await browser.BookmarksApiAsync("""
            async () => {
              const [folder] = await chrome.bookmarks.search({ title: "Bookmarks" });
              await chrome.bookmarks.removeTree(folder.id);
            }
            """);
        await browser.SyncBookmarksAsync();

        Assert.Equal("Mail", (await browser.BrowserFolderAsync("Bookmarks"))[0].GetProperty("title").GetString());
        Assert.Single(Stored(browser, Personal));
    }

    [Fact]
    public async Task A_file_no_longer_listed_takes_its_folder_with_it()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(ReadOnly, Html(Mark("a", "Handbook")));
        await browser.SyncBookmarksAsync();
        Assert.Single(await browser.BrowserFolderAsync("Team links"));

        browser.Bookmarks.Remove(ReadOnly.Id);
        await browser.SyncBookmarksAsync();

        Assert.Empty(await browser.BrowserFolderAsync("Team links"));
    }

    // Past the 1 MB a native-messaging frame may carry, in both directions.
    [Fact]
    public async Task A_file_larger_than_one_message_syncs_both_ways()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        var padding = new string('x', 3000);
        var marks = Enumerable.Range(0, 500).Select(i => Mark($"m{i}", $"Mark {i}", $"https://example.test/{i}/{padding}")).ToArray();
        browser.Bookmarks.Put(Personal, Html(marks));
        Assert.True(browser.Bookmarks.Html(Personal.Id).Length > 1024 * 1024);

        await browser.SyncBookmarksAsync();
        Assert.Equal(500, (await browser.BrowserFolderAsync("Bookmarks")).Count);

        await browser.BookmarksApiAsync("""
            async () => {
              const [folder] = await chrome.bookmarks.search({ title: "Bookmarks" });
              await chrome.bookmarks.create({ parentId: folder.id, title: "One more", url: "https://more.test/" });
            }
            """);
        await browser.SyncBookmarksAsync();

        Assert.Equal(501, Stored(browser, Personal).Count);
    }

    // An address this browser refuses is not this browser deleting it: another browser may hold it perfectly well.
    [Fact]
    public async Task An_address_the_browser_refuses_stays_in_the_file()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Pass);
        browser.Bookmarks.Put(Personal, Html(Mark("odd", "Odd", "not an address"), Mark("a", "Mail")));

        await browser.SyncBookmarksAsync();
        Assert.Equal(["Mail"], (await browser.BrowserFolderAsync("Bookmarks")).Select(n => n.GetProperty("title").GetString()));

        await browser.BookmarksApiAsync("""
            async () => {
              const [folder] = await chrome.bookmarks.search({ title: "Bookmarks" });
              await chrome.bookmarks.create({ parentId: folder.id, title: "New", url: "https://new.test/" });
            }
            """);
        await browser.SyncBookmarksAsync();

        var stored = Stored(browser, Personal);
        Assert.Contains(stored, n => n.Id == "odd");
        Assert.Contains(stored, n => n.Title == "New");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for {what}.");
            await Task.Delay(100);
        }
    }
}
