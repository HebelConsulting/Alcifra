namespace Alcifra.Api.WebDav;

/// <summary>An exclusive write lock on one path (ADR "WebDAV hardening").</summary>
public sealed record WebDavLock(string Token, Guid Owner, DateTimeOffset ExpiresAt);

/// <summary>
/// Where WebDAV locks live (#2176, ADR 1052). Two implementations, one contract, chosen as the throttle's counters are
/// (ADR 0716): in process for a single instance, in Valkey once there is more than one, because a client's next request
/// may reach another instance.
/// </summary>
public interface IWebDavLockStore
{
    /// <summary>A new exclusive lock, or the caller's own refreshed. Null when another owner holds it.</summary>
    Task<WebDavLock?> TryLockAsync(Guid tenantId, string path, Guid owner, TimeSpan timeout, DateTimeOffset now);

    Task<bool> UnlockAsync(Guid tenantId, string path, string token);

    /// <summary>Whether a different owner holds an unexpired lock whose token the request did not present.</summary>
    Task<bool> IsBlockedAsync(Guid tenantId, string path, Guid caller, IReadOnlyCollection<string> presentedTokens, DateTimeOffset now);
}
