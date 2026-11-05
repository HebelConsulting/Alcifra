using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.ApiClient.Bookmarks;

/// <summary>One bookmark file as a browser's sync sees it.</summary>
/// <param name="CanEdit">Whether the caller's changes are written back; otherwise it is synced read-only.</param>
/// <param name="Links">What reading and saving it follow.</param>
public sealed record BookmarkFileInfo(Guid Id, string Name, string FolderName, bool IsPersonal, bool CanEdit, bool HasContent, string ETag, LinkMap Links);

/// <summary>Somebody saved the file after it was read. A save merges again.</summary>
public sealed class BookmarkFileChangedException(string name)
    : Exception($"The bookmark file '{name}' was changed by somebody else while it was being saved.");

/// <summary>The file is gone, or the caller may no longer read it.</summary>
public sealed class BookmarkFileNotFoundException(Guid id)
    : Exception($"The bookmark file {id} no longer exists for this user.");

/// <summary>What a sync needs from the server, so the rule below runs the same over a real one and a test's.</summary>
public interface IBookmarkRemote
{
    Task<IReadOnlyList<BookmarkFileInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Makes the caller's personal file if they have none; a principal that keeps no browser has none to make.</summary>
    Task EnsurePersonalAsync(CancellationToken cancellationToken);

    Task<string> ReadAsync(BookmarkFileInfo file, CancellationToken cancellationToken);

    /// <summary>Saves under the tag <paramref name="file"/> was listed with.</summary>
    /// <exception cref="BookmarkFileChangedException">The tag is stale.</exception>
    Task SaveAsync(BookmarkFileInfo file, string html, CancellationToken cancellationToken);
}

/// <summary>The outcome of one file's sync: what the browser should now hold.</summary>
/// <param name="Saved">Whether a new version was written.</param>
/// <param name="Conflicts">How many bookmarks were edited on both sides at once.</param>
public sealed record BookmarkSyncOutcome(IReadOnlyList<BookmarkNode> Tree, bool Saved, int Conflicts);

/// <summary>
/// One file's sync (ADR 1062). The host holds nothing between calls: the browser brings what it last agreed with the
/// server and what it holds now, and takes the merged tree home.
/// </summary>
public static class BookmarkSync
{
    private const int Attempts = 4;

    /// <summary>The files the caller's browser keeps, their personal one made at the first sync.</summary>
    public static async Task<IReadOnlyList<BookmarkFileInfo>> FilesAsync(IBookmarkRemote remote, CancellationToken cancellationToken)
    {
        var files = await remote.ListAsync(cancellationToken);
        if (files.Any(f => f.IsPersonal))
        {
            return files;
        }

        await remote.EnsurePersonalAsync(cancellationToken);
        return await remote.ListAsync(cancellationToken);
    }

    /// <summary>Merges and saves; somebody saving in between means reading again and merging again (ADR 1062).</summary>
    /// <exception cref="BookmarkFileChangedException">The file kept changing through every attempt.</exception>
    /// <exception cref="BookmarkFileNotFoundException">The file is gone.</exception>
    public static async Task<BookmarkSyncOutcome> RunAsync(
        IBookmarkRemote remote, Guid fileId, IReadOnlyList<BookmarkNode>? @base, IReadOnlyList<BookmarkNode>? local, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var file = (await remote.ListAsync(cancellationToken)).FirstOrDefault(f => f.Id == fileId)
                ?? throw new BookmarkFileNotFoundException(fileId);
            var stored = file.HasContent ? BookmarkHtml.Parse(await remote.ReadAsync(file, cancellationToken)) : [];
            if (!file.IsPersonal)
            {
                stored = BookmarkTree.WebAddressesOnly(stored);
            }

            // A local change to a file the caller may not write is reverted by handing back what the server holds.
            if (!file.CanEdit)
            {
                return new BookmarkSyncOutcome(stored, false, 0);
            }

            var merged = file.IsPersonal
                ? BookmarkMerge.Merge(@base, local, stored)
                : BookmarkMerge.Merge(Web(@base), Web(local), stored);
            if (file.HasContent && BookmarkTree.Same(merged.Tree, stored))
            {
                return new BookmarkSyncOutcome(merged.Tree, false, merged.Conflicts);
            }

            try
            {
                await remote.SaveAsync(file, BookmarkHtml.Write(merged.Tree), cancellationToken);
                return new BookmarkSyncOutcome(merged.Tree, true, merged.Conflicts);
            }
            catch (BookmarkFileChangedException) when (attempt < Attempts)
            {
                // Read again: the next list carries the new tag and the next read the new content.
            }
        }
    }

    private static IReadOnlyList<BookmarkNode>? Web(IReadOnlyList<BookmarkNode>? tree) =>
        tree is null ? null : BookmarkTree.WebAddressesOnly(tree);
}

/// <summary>The server's bookmark files over the advertised rels (ADR 1062).</summary>
public sealed class BookmarksClient(ApiCore core) : IBookmarkRemote
{
    private const string FileExtension = ".html";

    public async Task<IReadOnlyList<BookmarkFileInfo>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await core.Http.GetAsync(await core.RootHrefAsync("bookmarks", cancellationToken), cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The bookmark files could not be read.", cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return
        [
            .. json.EnumerateArray().Select(f => new BookmarkFileInfo(
                f.GetProperty("id").GetGuid(),
                f.GetProperty("name").GetString() ?? string.Empty,
                f.GetProperty("folderName").GetString() ?? string.Empty,
                f.GetProperty("isPersonal").GetBoolean(),
                f.GetProperty("canEdit").GetBoolean(),
                f.GetProperty("hasContent").GetBoolean(),
                f.GetProperty("eTag").GetString() ?? string.Empty,
                LinkMap.From(f) ?? LinkMap.Empty)),
        ];
    }

    public async Task EnsurePersonalAsync(CancellationToken cancellationToken)
    {
        using var response = await core.Http.PostAsync(await core.RootHrefAsync("bookmarks", cancellationToken), null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            await ApiCore.ThrowIfProblemAsync(response, "The bookmark file could not be made.", cancellationToken);
        }
    }

    public async Task<string> ReadAsync(BookmarkFileInfo file, CancellationToken cancellationToken)
    {
        var versionHref = file.Links.Href("currentVersion")
            ?? throw new InvalidOperationException($"'{file.Name}' has no content this reader may fetch (no 'currentVersion' rel, ADR 0543).");
        using var response = await core.Http.GetAsync(versionHref, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, $"'{file.Name}' could not be read.", cancellationToken);
        var version = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var bytes = await ApiCore.GetContentBytesAsync(ApiCore.RequireRel(version, "download", file.Name), cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task SaveAsync(BookmarkFileInfo file, string html, CancellationToken cancellationToken)
    {
        using var created = await core.SendRelAsync(file.Links, "versions", new { fileExtension = FileExtension }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(created, $"'{file.Name}' could not be saved.", cancellationToken);
        var version = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var bytes = Encoding.UTF8.GetBytes(html);
        var encrypted = UploadEncryption.EncryptIfInstructed(version, bytes);
        using (var upload = new ByteArrayContent(encrypted?.Blob ?? bytes))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            using var put = await ApiCore.Anonymous.PutAsync(version.GetProperty("uploadUrl").GetString(), upload, cancellationToken);
            put.EnsureSuccessStatusCode();
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Put, ApiCore.RequireRel(version, "self", "The pending version"))
        {
            Content = JsonContent.Create(new { wrappedDek = encrypted?.WrappedDek, kekGeneration = encrypted?.KekGeneration }),
        };

        // A first version has no tag to carry: nothing has been seen yet.
        if (file.HasContent)
        {
            finalize.Headers.TryAddWithoutValidation("If-Match", file.ETag);
        }

        using var finalized = await core.Http.SendAsync(finalize, cancellationToken);
        if (finalized.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new BookmarkFileChangedException(file.Name);
        }

        await ApiCore.ThrowIfProblemAsync(finalized, $"'{file.Name}' could not be saved.", cancellationToken);
    }
}
