using System.Collections.Concurrent;

namespace Alcifra.Api.WebDav;

// In-memory WebDAV lock store (ADR "WebDAV hardening") for a single instance: exclusive write locks keyed by tenant +
// resource path, ephemeral and timeout-based. A lock is created by LOCK and enforced on the mutating methods: a
// *different* owner that doesn't present the matching lock token (via If / Lock-Token) gets 423 Locked.
public sealed class InMemoryWebDavLockStore : IWebDavLockStore
{
    private readonly ConcurrentDictionary<string, WebDavLock> _locks = new();

    private static string Key(Guid tenantId, string path) => $"{tenantId:D}:{path.Trim('/')}";

    private WebDavLock? Get(Guid tenantId, string path, DateTimeOffset now)
    {
        var key = Key(tenantId, path);
        if (_locks.TryGetValue(key, out var info))
        {
            if (info.ExpiresAt > now)
            {
                return info;
            }

            _locks.TryRemove(new KeyValuePair<string, WebDavLock>(key, info)); // expired — sweep lazily
        }

        return null;
    }

    public Task<WebDavLock?> TryLockAsync(Guid tenantId, string path, Guid owner, TimeSpan timeout, DateTimeOffset now)
    {
        var key = Key(tenantId, path);
        while (true)
        {
            if (_locks.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                if (existing.Owner != owner)
                {
                    return Task.FromResult<WebDavLock?>(null); // held by someone else
                }

                var refreshed = existing with { ExpiresAt = now + timeout };
                if (_locks.TryUpdate(key, refreshed, existing))
                {
                    return Task.FromResult<WebDavLock?>(refreshed);
                }

                continue; // lost a race — retry
            }

            var created = new WebDavLock($"opaquelocktoken:{Guid.NewGuid()}", owner, now + timeout);
            if (existing is null ? _locks.TryAdd(key, created) : _locks.TryUpdate(key, created, existing))
            {
                return Task.FromResult<WebDavLock?>(created);
            }
        }
    }

    public Task<bool> UnlockAsync(Guid tenantId, string path, string token)
    {
        var key = Key(tenantId, path);
        return Task.FromResult(_locks.TryGetValue(key, out var info) && info.Token == token
            && _locks.TryRemove(new KeyValuePair<string, WebDavLock>(key, info)));
    }

    public Task<bool> IsBlockedAsync(Guid tenantId, string path, Guid caller, IReadOnlyCollection<string> presentedTokens, DateTimeOffset now)
    {
        var info = Get(tenantId, path, now);
        return Task.FromResult(info is not null && info.Owner != caller && !presentedTokens.Contains(info.Token));
    }
}
