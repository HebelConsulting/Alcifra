using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.Pass.Infrastructure;

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
        PasswordFolderInfo store;
        if (string.IsNullOrWhiteSpace(name))
        {
            store = await session.Passwords.EnsureMyPasswordsAsync(cancellationToken);
        }
        else
        {
            var (stores, _) = await session.Passwords.ListStoresAsync(cancellationToken);
            store = await session.Passwords.GetFolderAsync(Resolve(stores, name).Href, cancellationToken);
        }

        await RememberStoreAsync(name, store.Id);
        return store;
    }

    /// <summary>
    /// The tree of the store <paramref name="name"/> names, from the cache alone, or null where it is not there or stale.
    /// For completion: a TAB answers from what the last command read, with no request, so it works while the installation
    /// is slow or the session has lapsed.
    /// </summary>
    public async Task<IReadOnlyList<PasswordTreeEntry>?> CachedRowsAsync(string? name)
    {
        if (await ReadStoresAsync() is not { } stores || !stores.TryGetValue(StoreKey(name), out var storeId))
        {
            return null;
        }

        return await ReadAsync(CachePath(session.Url, storeId)) is { } cached && DateTimeOffset.UtcNow - cached.Written < CacheFor
            ? cached.Rows
            : null;
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
            [] => throw new PassException(
                $"No password store named '{name}'. The stores you may see: {string.Join(", ", stores.Select(s => $"'{s.Label}'"))}."),
            _ when named.Count(s => s.IsPersonal) == 1 => named.Single(s => s.IsPersonal),
            _ => throw new PassException(
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
            PassLog.For<StoreCatalog>().LogDebug("Using the cached tree of {StoreId} ({Count} rows).", store.Id, cached.Rows.Count);
            return cached.Rows;
        }

        var rows = await FetchAsync(store, cancellationToken);
        await WriteAsync(path, rows);
        return rows;
    }

    /// <summary>The row at <paramref name="path"/>, or a sentence naming what is not there.</summary>
    public static PasswordTreeEntry Find(IReadOnlyList<PasswordTreeEntry> rows, string path) =>
        PasswordTree.FindOrNull(rows, path) ?? throw new PassException($"No folder or entry at '{PasswordTree.Normalise(path)}'.");

    private async Task<List<PasswordTreeEntry>> FetchAsync(PasswordFolderInfo store, CancellationToken cancellationToken)
    {
        var href = store.Links.Href("descendants")
            ?? throw new PassException(
                "The store offers no descendant listing; the installation is older than this tool. Update the installation, or use the apps.");

        return [.. PasswordTree.WithPaths(store.Id, await session.Passwords.AllDescendantsAsync(href, cancellationToken))];
    }

    // ---- The cache: one file per (installation, store), ignored when unreadable and rewritten on a cold read ----

    private sealed record CachedTree(DateTimeOffset Written, List<PasswordTreeEntry> Rows);

    // LocalApplicationData rather than a home-relative .cache: the platform's own place for disposable data, and the same
    // call on every OS this tool ships for. ALCIFRA_CACHE_DIR moves it, for a test that must not touch a real cache.
    internal static string CacheFolder =>
        Environment.GetEnvironmentVariable("ALCIFRA_CACHE_DIR") is { Length: > 0 } given
            ? given
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "apass");

    private static string CachePath(string url, Guid storeId)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{url}|{storeId:D}")))[..16];
        return Path.Combine(CacheFolder, $"tree-{key}.json");
    }

    // Which store a name meant when a command last resolved it, per installation: the key the tree cache needs.
    private string StoresPath => Path.Combine(CacheFolder,
        $"stores-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Url)))[..16]}.json");

    private static string StoreKey(string? name) => string.IsNullOrWhiteSpace(name) ? string.Empty : name;

    private async Task<Dictionary<string, Guid>?> ReadStoresAsync()
    {
        try
        {
            return File.Exists(StoresPath) ? JsonSerializer.Deserialize<Dictionary<string, Guid>>(await File.ReadAllTextAsync(StoresPath)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            PassLog.For<StoreCatalog>().LogDebug("Ignoring an unreadable store cache at {Path}: {Reason}", StoresPath, e.Message);
            return null;
        }
    }

    private async Task RememberStoreAsync(string? name, Guid storeId)
    {
        var stores = await ReadStoresAsync() ?? [];
        if (stores.TryGetValue(StoreKey(name), out var known) && known == storeId)
        {
            return;
        }

        stores[StoreKey(name)] = storeId;
        try
        {
            Directory.CreateDirectory(CacheFolder);
            await File.WriteAllTextAsync(StoresPath, JsonSerializer.Serialize(stores));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PassLog.For<StoreCatalog>().LogDebug("Could not write the store cache at {Path}: {Reason}", StoresPath, e.Message);
        }
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
            PassLog.For<StoreCatalog>().LogDebug("Ignoring an unreadable tree cache at {Path}: {Reason}", path, e.Message);
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
            PassLog.For<StoreCatalog>().LogDebug("Could not write the tree cache at {Path}: {Reason}", path, e.Message);
        }
    }
}
