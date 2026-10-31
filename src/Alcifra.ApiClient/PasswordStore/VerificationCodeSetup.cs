using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>The hash a time-based code is computed with (RFC 6238 allows three).</summary>
public enum VerificationCodeAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// The setup behind an entry's verification code (ADR 1013): the shared secret a site hands out once, as a link
/// (<c>otpauth://totp/…</c>, usually inside its QR code) or as a bare setup key, and the code it yields at any moment
/// (RFC 6238). <b>The one implementation every client uses</b> — the desktop, the phone, the browser host and the
/// command-line tool show the same code because they compute it here, never with a second copy.
/// </summary>
/// <remarks>
/// <para>
/// A bare key means the usual defaults: SHA-1, six digits, thirty seconds. Spaces, hyphens, padding and case in a key are
/// ignored, as sites print it in groups and people type it in either case.
/// </para>
/// <para>
/// The secret never leaves this type except through <see cref="ToUri"/>, which builds the value the entry stores;
/// <see cref="ToString"/> deliberately leaves it out, so a setup that reaches a log line carries nothing usable.
/// </para>
/// </remarks>
public sealed class VerificationCodeSetup
{
    private const string Scheme = "otpauth";
    private const string TimeBased = "totp";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly byte[] _secret;

    private VerificationCodeSetup(byte[] secret, string secretText, VerificationCodeAlgorithm algorithm, int digits, int period, string? issuer, string? account)
    {
        _secret = secret;
        SecretText = secretText;
        Algorithm = algorithm;
        Digits = digits;
        Period = period;
        Issuer = issuer;
        Account = account;
    }

    /// <summary>The hash the code is computed with.</summary>
    public VerificationCodeAlgorithm Algorithm { get; }

    /// <summary>How many digits a code has: 6 or 8.</summary>
    public int Digits { get; }

    /// <summary>How many seconds one code is valid for.</summary>
    public int Period { get; }

    /// <summary>The issuer a link named (the site), or null.</summary>
    public string? Issuer { get; }

    /// <summary>The account a link named (usually the user name), or null.</summary>
    public string? Account { get; }

    // The secret in its normalised base32 spelling, for ToUri only.
    private string SecretText { get; }

    /// <summary>Whether <paramref name="value"/> reads as a setup (a link or a bare key); no reason is given.</summary>
    public static bool TryParse(string? value, out VerificationCodeSetup? setup)
    {
        try
        {
            setup = Parse(value);
            return true;
        }
        catch (VerificationCodeSetupException)
        {
            setup = null;
            return false;
        }
    }

    /// <summary>
    /// Reads an <c>otpauth://totp/…</c> link or a bare base32 setup key.
    /// </summary>
    /// <exception cref="VerificationCodeSetupException">One of its specific subclasses, naming what is wrong — never
    /// the secret itself.</exception>
    public static VerificationCodeSetup Parse(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new VerificationCodeSetupEmptyException();
        }

        return text.StartsWith($"{Scheme}:", StringComparison.OrdinalIgnoreCase) ? FromLink(text) : FromKey(text, VerificationCodeAlgorithm.Sha1, 6, 30, null, null);
    }

    private static VerificationCodeSetup FromLink(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
        {
            throw new VerificationCodeLinkUnreadableException();
        }

        // A counter-based link (hotp) yields codes this store cannot keep in step; only time-based ones are taken.
        if (!string.Equals(uri.Host, TimeBased, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotATimeBasedCodeException(uri.Host);
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(cut < 0 ? pair : pair[..cut]);
            var parameter = cut < 0 ? string.Empty : Uri.UnescapeDataString(pair[(cut + 1)..].Replace('+', ' '));
            query.TryAdd(name, parameter);
        }

        var secret = Parameter(query, "secret");
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new VerificationCodeSecretMissingException();
        }

        var algorithm = Parameter(query, "algorithm")?.Trim().ToUpperInvariant() switch
        {
            null or "" or "SHA1" => VerificationCodeAlgorithm.Sha1,
            "SHA256" => VerificationCodeAlgorithm.Sha256,
            "SHA512" => VerificationCodeAlgorithm.Sha512,
            var other => throw new UnsupportedCodeAlgorithmException(other),
        };
        var digits = Parameter(query, "digits")?.Trim() switch
        {
            null or "" or "6" => 6,
            "8" => 8,
            var other => throw new UnsupportedCodeDigitsException(other),
        };
        var periodText = Parameter(query, "period")?.Trim();
        var period = string.IsNullOrEmpty(periodText) ? 30
            : int.TryParse(periodText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 3600 ? seconds
            : throw new UnsupportedCodePeriodException(periodText);

        // The label is "Issuer:account" or just "account"; an issuer parameter wins over the label's prefix.
        var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var colon = label.IndexOf(':', StringComparison.Ordinal);
        var labelIssuer = colon < 0 ? null : label[..colon].Trim();
        var account = (colon < 0 ? label : label[(colon + 1)..]).Trim();
        var issuer = Parameter(query, "issuer")?.Trim();

        return FromKey(secret, algorithm, digits, period, NullIfEmpty(issuer) ?? NullIfEmpty(labelIssuer), NullIfEmpty(account));
    }

    private static VerificationCodeSetup FromKey(string key, VerificationCodeAlgorithm algorithm, int digits, int period, string? issuer, string? account)
    {
        var normalised = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is ' ' or '-' or '=' || char.IsWhiteSpace(c))
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c);
            if (!Base32Alphabet.Contains(upper, StringComparison.Ordinal))
            {
                throw new VerificationCodeSecretNotBase32Exception();
            }

            normalised.Append(upper);
        }

        var secret = Base32Decode(normalised.ToString());
        if (secret.Length == 0)
        {
            throw new VerificationCodeSecretNotBase32Exception();
        }

        return new VerificationCodeSetup(secret, normalised.ToString(), algorithm, digits, period, issuer, account);
    }

    private static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // A link's query parameter, or null; a helper rather than a dictionary lookup inline, which reads like a followed rel.
    private static string? Parameter(Dictionary<string, string> query, string name) => query.TryGetValue(name, out var value) ? value : null;

    /// <summary>The code valid at <paramref name="instant"/> (RFC 6238 over RFC 4226's truncation).</summary>
    public string CodeAt(DateTimeOffset instant)
    {
        var counter = instant.ToUnixTimeSeconds() / Period;
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = Algorithm switch
        {
            VerificationCodeAlgorithm.Sha256 => HMACSHA256.HashData(_secret, message),
            VerificationCodeAlgorithm.Sha512 => HMACSHA512.HashData(_secret, message),
            _ => HMACSHA1.HashData(_secret, message),
        };

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = Digits == 8 ? 100_000_000 : 1_000_000;
        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>The code valid now on <paramref name="clock"/>, and how many seconds it stays valid.</summary>
    public VerificationCode Current(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var elapsed = (int)(now.ToUnixTimeSeconds() % Period);
        return new VerificationCode(CodeAt(now), Period - elapsed, Period);
    }

    /// <summary>
    /// The value an entry stores (ADR 1013): the standard link, with the label given (usually the entry's name) where the
    /// setup named no account, and the non-default parameters spelled out. A link read from a site keeps its issuer and
    /// account.
    /// </summary>
    public string ToUri(string? label = null)
    {
        var account = Account ?? NullIfEmpty(label?.Trim()) ?? "code";
        var path = Issuer is null ? Uri.EscapeDataString(account) : $"{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(account)}";
        var query = new StringBuilder($"secret={SecretText}");
        if (Issuer is not null)
        {
            query.Append(CultureInfo.InvariantCulture, $"&issuer={Uri.EscapeDataString(Issuer)}");
        }

        if (Algorithm != VerificationCodeAlgorithm.Sha1)
        {
            query.Append(CultureInfo.InvariantCulture, $"&algorithm={Algorithm.ToString().ToUpperInvariant()}");
        }

        if (Digits != 6)
        {
            query.Append(CultureInfo.InvariantCulture, $"&digits={Digits}");
        }

        if (Period != 30)
        {
            query.Append(CultureInfo.InvariantCulture, $"&period={Period}");
        }

        return $"{Scheme}://{TimeBased}/{path}?{query}";
    }

    /// <summary>Names the setup without its secret: safe in a log line.</summary>
    public override string ToString() => $"{Algorithm}, {Digits} digits, {Period} s";
}

/// <summary>A code as shown: its digits, the seconds it stays valid, and its period (for a countdown's full length).</summary>
public readonly record struct VerificationCode(string Value, int SecondsLeft, int Period)
{
    /// <summary>The digits in two groups, as every client shows them: "123 456", "1234 5678".</summary>
    public string Grouped => Value.Length < 6 ? Value : $"{Value[..(Value.Length / 2)]} {Value[(Value.Length / 2)..]}";
}

/// <summary>A value that is not a usable verification-code setup; each subclass names what is wrong, never the secret.</summary>
public abstract class VerificationCodeSetupException(string message) : Exception(message);

/// <summary>Nothing was given.</summary>
public sealed class VerificationCodeSetupEmptyException()
    : VerificationCodeSetupException("No setup key or link was given.");

/// <summary>A link that does not read as an <c>otpauth://</c> address.</summary>
public sealed class VerificationCodeLinkUnreadableException()
    : VerificationCodeSetupException("The link is not a readable otpauth:// address.");

/// <summary>A link for a kind of code other than time-based (a counter-based "hotp" one, say).</summary>
public sealed class NotATimeBasedCodeException(string kind)
    : VerificationCodeSetupException($"Only time-based codes are supported, not '{kind}'.");

/// <summary>A link without its secret.</summary>
public sealed class VerificationCodeSecretMissingException()
    : VerificationCodeSetupException("The link carries no secret.");

/// <summary>A secret or key that is not base32 (letters A–Z and digits 2–7).</summary>
public sealed class VerificationCodeSecretNotBase32Exception()
    : VerificationCodeSetupException("The key is not a base32 setup key (letters A to Z and digits 2 to 7).");

/// <summary>A hash other than SHA1, SHA256 or SHA512.</summary>
public sealed class UnsupportedCodeAlgorithmException(string algorithm)
    : VerificationCodeSetupException($"The algorithm '{algorithm}' is not supported; SHA1, SHA256 and SHA512 are.");

/// <summary>A digit count other than 6 or 8.</summary>
public sealed class UnsupportedCodeDigitsException(string digits)
    : VerificationCodeSetupException($"Codes of '{digits}' digits are not supported; 6 and 8 are.");

/// <summary>A period that is not a whole number of seconds between 1 and 3600.</summary>
public sealed class UnsupportedCodePeriodException(string period)
    : VerificationCodeSetupException($"The period '{period}' is not supported; a whole number of seconds up to 3600 is.");
