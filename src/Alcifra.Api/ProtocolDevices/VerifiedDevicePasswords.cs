using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Alcifra.Domain.Users;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// That a device password was verified a moment ago, so a burst of protocol requests does not pay the deliberately slow hash
/// on every one (#2161, ADR 1050). A mount sends hundreds of requests for a few files, each with the password.
/// </summary>
/// <remarks>
/// <para>
/// What is kept is a keyed fingerprint under a key this process makes at start and never shares, so it is worthless outside
/// this memory, and is not a password or a hash of one that could be attacked offline. In memory only, per instance, for
/// <see cref="Lifetime"/> from the verification, never extended by use.
/// </para>
/// <para>
/// It only saves the hash. The caller still reads the user and the device on every request and compares the stored hash, so a
/// revoked device, a switch turned off, a deactivated user or a changed password take effect at once.
/// </para>
/// </remarks>
public sealed class VerifiedDevicePasswords
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    // A bound against a flood of distinct valid passwords, which only an authenticated caller can add to.
    private const int Capacity = 10_000;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Verified> _verified = new(StringComparer.Ordinal);

    private long _remembered;

    /// <summary>How many passwords the slow hash has verified since start: what the upload replay holds to one (ADR 1051).</summary>
    public long Remembered => Interlocked.Read(ref _remembered);

    private sealed record Verified(Guid DeviceId, string SecretHash, DateTimeOffset Until);

    /// <summary>The device this password opened for this user and protocol within the lifetime, and the hash it was checked against.</summary>
    public (Guid DeviceId, string SecretHash)? Find(Guid userId, string password, DeviceProtocol protocol, DateTimeOffset now)
    {
        var fingerprint = Fingerprint(userId, password, protocol);
        if (!_verified.TryGetValue(fingerprint, out var verified))
        {
            return null;
        }

        if (verified.Until <= now)
        {
            _verified.TryRemove(fingerprint, out _);
            return null;
        }

        return (verified.DeviceId, verified.SecretHash);
    }

    public void Remember(Guid userId, string password, DeviceProtocol protocol, ProtocolDevice device, DateTimeOffset now)
    {
        if (_verified.Count >= Capacity)
        {
            foreach (var (fingerprint, verified) in _verified)
            {
                if (verified.Until <= now)
                {
                    _verified.TryRemove(fingerprint, out _);
                }
            }

            if (_verified.Count >= Capacity)
            {
                _verified.Clear();
            }
        }

        _verified[Fingerprint(userId, password, protocol)] = new Verified(device.Id, device.SecretHash, now + Lifetime);
        Interlocked.Increment(ref _remembered);
    }

    private string Fingerprint(Guid userId, string password, DeviceProtocol protocol) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{userId:N}\n{protocol}\n{password}")));
}
