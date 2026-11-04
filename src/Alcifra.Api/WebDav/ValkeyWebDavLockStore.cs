using Alcifra.Api.Errors.Exceptions.WebDav;
using StackExchange.Redis;

namespace Alcifra.Api.WebDav;

/// <summary>
/// The shared lock store, used when <c>ConnectionStrings:Valkey</c> is set (#2176, ADR 1052). Each lock is a hash whose TTL
/// is its timeout, so an abandoned one expires by itself. A script takes it, because checking the owner and writing must be
/// one step when two instances race.
/// </summary>
/// <remarks>
/// When Valkey cannot answer, every operation throws <see cref="WebDavLocksUnavailableException"/> (503). A client must never
/// be told it holds a lock nobody can enforce, and a write must not pass a check nobody could make.
/// </remarks>
public sealed class ValkeyWebDavLockStore : IWebDavLockStore
{
    private const string Prefix = "alcifra:webdav-lock:";
    private const string OwnerField = "owner";
    private const string TokenField = "token";

    private const string TryLockScript = """
        local owner = redis.call('HGET', KEYS[1], 'owner')
        if owner and owner ~= ARGV[1] then return false end
        if owner then
          redis.call('PEXPIRE', KEYS[1], ARGV[3])
          return redis.call('HGET', KEYS[1], 'token')
        end
        redis.call('HSET', KEYS[1], 'owner', ARGV[1], 'token', ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return ARGV[2]
        """;

    private const string UnlockScript = """
        if redis.call('HGET', KEYS[1], 'token') == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<ValkeyWebDavLockStore> _logger;

    public ValkeyWebDavLockStore(IConnectionMultiplexer multiplexer, ILogger<ValkeyWebDavLockStore> logger) =>
        (_multiplexer, _logger) = (multiplexer, logger);

    private static RedisKey Key(Guid tenantId, string path) => $"{Prefix}{tenantId:D}:{path.Trim('/')}";

    public Task<WebDavLock?> TryLockAsync(Guid tenantId, string path, Guid owner, TimeSpan timeout, DateTimeOffset now) =>
        AnsweredAsync<WebDavLock?>(async () =>
        {
            var token = await _multiplexer.GetDatabase().ScriptEvaluateAsync(
                TryLockScript,
                [Key(tenantId, path)],
                [owner.ToString("D"), $"opaquelocktoken:{Guid.NewGuid()}", (long)timeout.TotalMilliseconds]);

            return token.IsNull ? null : new WebDavLock(token.ToString(), owner, now + timeout);
        });

    public Task<bool> UnlockAsync(Guid tenantId, string path, string token) =>
        AnsweredAsync(async () =>
            (long)await _multiplexer.GetDatabase().ScriptEvaluateAsync(UnlockScript, [Key(tenantId, path)], [token]) > 0);

    public Task<bool> IsBlockedAsync(Guid tenantId, string path, Guid caller, IReadOnlyCollection<string> presentedTokens, DateTimeOffset now) =>
        AnsweredAsync(async () =>
        {
            var held = await _multiplexer.GetDatabase().HashGetAsync(Key(tenantId, path), [OwnerField, TokenField]);

            return !held[0].IsNull && held[0] != caller.ToString("D") && !presentedTokens.Contains(held[1].ToString());
        });

    private async Task<T> AnsweredAsync<T>(Func<Task<T>> ask)
    {
        try
        {
            return await ask();
        }
        catch (Exception e) when (e is RedisConnectionException or RedisTimeoutException)
        {
            _logger.LogWarning(e, "WebDAV locks cannot reach Valkey; the request is refused with 503");
            throw new WebDavLocksUnavailableException();
        }
    }
}
