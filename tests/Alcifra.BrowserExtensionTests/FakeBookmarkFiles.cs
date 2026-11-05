using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Bookmarks;

namespace Alcifra.BrowserExtensionTests;

/// <summary>One bookmark file the fake server holds.</summary>
public sealed record FakeBookmarkFile(Guid Id, string Name, bool IsPersonal, bool CanEdit, int Tag = 0);

/// <summary>
/// A server's bookmark files with no server: the list and each file's HTML live in the state folder, because each browser
/// request runs in its own host process. A save is refused on a stale tag, as the real confirm is.
/// </summary>
public sealed class FakeBookmarkFiles(string stateFolder) : IBookmarkRemote
{
    private string ListPath => Path.Combine(stateFolder, "bookmark-files.json");

    private string HtmlPath(Guid id) => Path.Combine(stateFolder, $"bookmarks-{id}.html");

    /// <summary>Puts a file on the fake server, or replaces its content as somebody else's save would, moving its tag.</summary>
    public void Put(FakeBookmarkFile file, string html)
    {
        var files = Files();
        var tag = files.FirstOrDefault(f => f.Id == file.Id)?.Tag ?? -1;
        files.RemoveAll(f => f.Id == file.Id);
        files.Add(file with { Tag = tag + 1 });
        Directory.CreateDirectory(stateFolder);
        File.WriteAllText(ListPath, JsonSerializer.Serialize(files));
        File.WriteAllText(HtmlPath(file.Id), html);
    }

    /// <summary>Takes a file off the list, as a deletion or a lost right would.</summary>
    public void Remove(Guid id)
    {
        var files = Files();
        files.RemoveAll(f => f.Id == id);
        File.WriteAllText(ListPath, JsonSerializer.Serialize(files));
    }

    public string Html(Guid id) => File.ReadAllText(HtmlPath(id));

    public Task<IReadOnlyList<BookmarkFileInfo>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookmarkFileInfo>>(
        [
            .. Files().Select(f => new BookmarkFileInfo(f.Id, f.Name, "My Documents", f.IsPersonal, f.CanEdit, File.Exists(HtmlPath(f.Id)), $"\"{f.Tag}\"", LinkMap.Empty)),
        ]);

    public Task EnsurePersonalAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> ReadAsync(BookmarkFileInfo file, CancellationToken cancellationToken) => Task.FromResult(Html(file.Id));

    public Task SaveAsync(BookmarkFileInfo file, string html, CancellationToken cancellationToken)
    {
        var stored = Files().Single(f => f.Id == file.Id);
        if ($"\"{stored.Tag}\"" != file.ETag)
        {
            throw new BookmarkFileChangedException(file.Name);
        }

        Put(stored, html);
        return Task.CompletedTask;
    }

    private List<FakeBookmarkFile> Files() =>
        File.Exists(ListPath) ? JsonSerializer.Deserialize<List<FakeBookmarkFile>>(File.ReadAllText(ListPath)) ?? [] : [];
}
