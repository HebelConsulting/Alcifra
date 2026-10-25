using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.Sapass.Infrastructure;

/// <summary>
/// The store's whole tree, read once and kept briefly — what <c>ls</c>, <c>find</c>, completion and the write
/// commands all resolve a path against. The tree MODEL (paths, children) is the shared client's
/// (<see cref="PasswordTree"/>); this class adds the two things a terminal needs: choosing the store by name, and a
/// short cache so a keystroke is not a request.
/// </summary>
/// <remarks>
/// <b>Names are in the clear (ADR 0964), so reading the tree needs no key and no PIN.</b> That is what makes
/// completion possible at all: the candidate list is public data about the user's own store.
/// </remarks>
internal sealed class StoreCatalog(Session session)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>The store to read: the named one, or My Passwords (created on first use, as in the apps).</summary>
    public async Task<PasswordFolderInfo> StoreAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return await session.Passwords.EnsureMyPasswordsAsync(cancellationToken);
        }

        var (stores, _) = await session.Passwords.ListStoresAsync(cancellationToken);
        return await session.Passwords.GetFolderAsync(Resolve(stores, name).Href, cancellationToken);
    }

    /// <summary>
    /// The store <paramref name="name"/> means: its full, parent-qualified name (<c>Anna Berger / My Passwords</c>,
    /// ADR 0619), or its plain name while that names one store. Where the plain name is shared — an administrator sees
    /// other users' My Passwords (the personal-space x-ray, ADR 0670) — the caller's OWN store wins, as with no <c>--store</c>;
    /// otherwise the tool refuses and lists the full names to choose from, rather than picking one.
    /// </summary>
    internal static PasswordStoreInfo Resolve(IReadOnlyList<PasswordStoreInfo> stores, string name)
    {
        if (stores.Where(s => string.Equals(s.Label, name, StringComparison.Ordinal)).ToList() is [var exact])
        {
            return exact;
        }

        var named = stores.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();
        return named switch
        {
            [var only] => only,
            [] => throw new SapassException(
                $"No password store named '{name}'. The stores you may see: {string.Join(", ", stores.Select(s => $"'{s.Label}'"))}."),
            _ when named.Count(s => s.IsPersonal) == 1 => named.Single(s => s.IsPersonal),
            _ => throw new SapassException(
                $"'{name}' names {named.Count} password stores: {string.Join(", ", named.Select(s => $"'{s.Label}'"))}. Give the full name with --store."),
        };
    }

    /// <summary>
    /// What <c>--store</c> completes to: My Passwords for the caller's own, then each other store by its plain name while
    /// that is unique, else by its full name — every candidate one <see cref="Resolve"/> accepts.
    /// </summary>
    internal static IReadOnlyList<string> CompletionNames(IReadOnlyList<PasswordStoreInfo> stores, bool offersMyPasswords)
    {
        var shared = stores.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. offersMyPasswords || stores.Any(s => s.IsPersonal) ? [PersonalFolderName] : Array.Empty<string>(),
            .. stores.Where(s => !s.IsPersonal).Select(s => shared.Contains(s.Name) ? s.Label : s.Name),
        ];
    }

    // The caller's own store's name, which resolves to it even where other users' share it (see Resolve).
    private const string PersonalFolderName = "My Passwords";

    /// <summary>Every folder and entry beneath the store, with its path, from the cache unless stale or refreshed.</summary>
    public async Task<IReadOnlyList<PasswordTreeEntry>> RowsAsync(PasswordFolderInfo store, bool refresh, CancellationToken cancellationToken)
    {
        var path = CachePath(session.Url, store.Id);
        if (!refresh && await ReadAsync(path) is { } cached && DateTimeOffset.UtcNow - cached.Written < CacheFor)
        {
            SapassLog.For<StoreCatalog>().LogDebug("Using the cached tree of {StoreId} ({Count} rows).", store.Id, cached.Rows.Count);
            return cached.Rows;
        }

        var rows = await FetchAsync(store, cancellationToken);
        await WriteAsync(path, rows);
        return rows;
    }

    /// <summary>The row at <paramref name="path"/>, or a sentence naming what is not there.</summary>
    public static PasswordTreeEntry Find(IReadOnlyList<PasswordTreeEntry> rows, string path) =>
        PasswordTree.FindOrNull(rows, path) ?? throw new SapassException($"No folder or entry at '{PasswordTree.Normalise(path)}'.");

    private async Task<List<PasswordTreeEntry>> FetchAsync(PasswordFolderInfo store, CancellationToken cancellationToken)
    {
        var href = store.Links.Href("descendants")
            ?? throw new SapassException(
                "The store offers no descendant listing; the installation is older than this tool. Update the installation, or use the apps.");

        return [.. PasswordTree.WithPaths(store.Id, await session.Passwords.AllDescendantsAsync(href, cancellationToken))];
    }

    // ---- The cache: one file per (installation, store), ignored when unreadable and rewritten on a cold read ----

    private sealed record CachedTree(DateTimeOffset Written, List<PasswordTreeEntry> Rows);

    private static string CachePath(string url, Guid storeId)
    {
        // LocalApplicationData rather than a home-relative .cache: the platform's own place for disposable data,
        // and the same call on every OS this tool ships for.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sapass");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{url}|{storeId:D}")))[..16];
        return Path.Combine(folder, $"tree-{key}.json");
    }

    private static async Task<CachedTree?> ReadAsync(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CachedTree>(await File.ReadAllTextAsync(path))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The cache is an optimisation; a damaged one is ignored, never a reason a command fails.
            SapassLog.For<StoreCatalog>().LogDebug("Ignoring an unreadable tree cache at {Path}: {Reason}", path, e.Message);
            return null;
        }
    }

    private static async Task WriteAsync(string path, List<PasswordTreeEntry> rows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CachedTree(DateTimeOffset.UtcNow, rows)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SapassLog.For<StoreCatalog>().LogDebug("Could not write the tree cache at {Path}: {Reason}", path, e.Message);
        }
    }
}
