using System.Collections.Concurrent;

namespace Alcifra.Api.WebDav;

/// <summary>
/// That a user's personal space was provisioned a moment ago, so a burst of WebDAV requests does not repeat it on every one
/// (#2179, ADR 1054). Provisioning is 11 of the 18 queries behind even a hidden file's 404.
/// </summary>
/// <remarks>
/// In memory only, per instance, for <see cref="Lifetime"/> from the check, never extended by use. It only saves the
/// provisioning: the caller still reads the space's current name on every request and provisions again when it is gone, so a
/// renamed or removed space is seen at once. A typed folder deleted meanwhile comes back within the lifetime.
/// </remarks>
public sealed class CheckedPersonalSpaces
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const int Capacity = 10_000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _until = new();
    private long _checks;

    /// <summary>How many provisioning runs this memory has recorded since start: what the upload replay holds to one (ADR 1054).</summary>
    public long Checks => Interlocked.Read(ref _checks);

    public bool IsChecked(Guid userId, DateTimeOffset now) => _until.TryGetValue(userId, out var until) && until > now;

    public void Remember(Guid userId, DateTimeOffset now)
    {
        if (_until.Count >= Capacity)
        {
            _until.Clear();
        }

        _until[userId] = now + Lifetime;
        Interlocked.Increment(ref _checks);
    }
}
