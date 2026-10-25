using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.Sapass.Infrastructure;

/// <summary>
/// Creates an entry for <c>insert</c> and <c>generate</c>: resolves the folder, creates the folders the path names
/// that do not exist yet (pass's shape), and files the secret sealed to the store's readers.
/// </summary>
/// <remarks>
/// <b>It never overwrites.</b> Repointing an existing entry is an EDIT — it opens the current content with the key
/// and writes a new version under the entry's precondition (ADR 0794) — and a create path that silently replaced a
/// secret would be the worst possible place to be clever.
/// </remarks>
internal static class EntryWriter
{
    public static async Task<PasswordEntryInfo> CreateAsync(
        Session session, string? storeName, string path, PasswordEntryContent content, bool refresh, CancellationToken cancellationToken)
    {
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(storeName, cancellationToken);
        var rows = await catalog.RowsAsync(store, refresh, cancellationToken);

        var (folderPath, name) = Passwords.Split(path);
        if (name.Length == 0)
        {
            throw new SapassException($"'{path}' does not name an entry; give a path like Mail/Banking.");
        }

        if (rows.FirstOrDefault(r => string.Equals(r.Path, PasswordTree.Normalise(path), StringComparison.Ordinal)) is { } existing)
        {
            throw new SapassException(
                $"'{existing.Path}' already exists ({(existing.IsFolder ? "a folder" : "an entry")}). Creating never replaces a secret; pick another name.");
        }

        var folder = await FolderAsync(session, store, rows, folderPath, cancellationToken);
        return await session.Passwords.CreateEntryAsync(folder, name, content, cancellationToken);
    }

    /// <summary>The folder at <paramref name="folderPath"/> below the store, creating the segments that are missing.</summary>
    private static async Task<PasswordFolderInfo> FolderAsync(
        Session session, PasswordFolderInfo store, IReadOnlyList<PasswordTreeEntry> rows, string folderPath, CancellationToken cancellationToken)
    {
        var current = store;
        var currentPath = string.Empty;
        foreach (var segment in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}/{segment}";
            var row = rows.FirstOrDefault(r => string.Equals(r.Path, currentPath, StringComparison.Ordinal));
            if (row is { IsFolder: false })
            {
                throw new SapassException($"'{currentPath}' is an entry, so it cannot hold '{folderPath}'.");
            }

            current = row is null
                ? await session.Passwords.CreateFolderAsync(current, segment, cancellationToken)
                : await session.Passwords.GetFolderAsync(row.Href, cancellationToken);
        }

        return current;
    }
}
