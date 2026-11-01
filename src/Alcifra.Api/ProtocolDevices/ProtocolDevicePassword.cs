using System.Security.Cryptography;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>
/// An enrolled device's password, <c>&lt;key&gt;.&lt;secret&gt;</c> (#1970, owner 2026-10-29): the one place it is made and
/// read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key finds the device, the secret proves it.</b> The key is public and unique installation-wide, so a login
/// verifies exactly ONE deliberately slow hash rather than every device of the user, on every DAV request. Only the
/// secret is hashed.
/// </para>
/// <para>
/// <b>Never confused with a password from before enrolment.</b> Those are 32 lowercase hex characters with no dot, so
/// a password without this shape is tried only against the user's legacy devices. Both halves are Basic-auth safe (no
/// colon) and need no escaping in a URL or a configuration profile.
/// </para>
/// </remarks>
public static class ProtocolDevicePassword
{
    /// <summary>The key's length: lowercase base32, 40 bits. Collisions are caught by the unique index and retried.</summary>
    public const int KeyLength = 8;

    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A new key, secret, and the password the user types (the two joined by a dot).</summary>
    public static (string Key, string Secret, string Password) Generate()
    {
        var key = RandomNumberGenerator.GetString(KeyAlphabet, KeyLength);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return (key, secret, $"{key}.{secret}");
    }

    /// <summary>
    /// Who the sign-in throttle counts a failed attempt against (ADR 0716; owner, 2026-10-31): an enrolled device's
    /// password under its OWN key, so a revoked phone that keeps retrying throttles only itself; anything else under the
    /// account, as before. The per-address counter still stops spraying either way.
    /// </summary>
    public static string ThrottleIdentity(string normalizedUserName, string password) =>
        TryParse(password, out var key, out _) ? $"{normalizedUserName}#{key}" : normalizedUserName;

    /// <summary>Splits a typed password into its key and secret; false for anything else (a legacy password).</summary>
    public static bool TryParse(string password, out string key, out string secret)
    {
        key = string.Empty;
        secret = string.Empty;
        if (password.Length <= KeyLength + 1 || password[KeyLength] != '.')
        {
            return false;
        }

        var candidate = password[..KeyLength];
        if (!candidate.All(KeyAlphabet.Contains))
        {
            return false;
        }

        key = candidate;
        secret = password[(KeyLength + 1)..];
        return true;
    }
}
