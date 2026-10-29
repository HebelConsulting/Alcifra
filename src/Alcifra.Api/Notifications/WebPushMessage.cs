using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Alcifra.Api.Notifications;

/// <summary>
/// A Web Push message, encrypted to one device (RFC 8291, content coding <c>aes128gcm</c>, RFC 8188) and authorised
/// with the installation's VAPID key (RFC 8292).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the WebPush package's own encryption.</b> It produces the older draft coding <c>aesgcm</c>, which puts
/// the salt and the sender's key in HTTP headers. A UnifiedPush distributor hands the app the BODY only, so a phone
/// could never decrypt it. <c>aes128gcm</c> carries both inside the body (found by the phone push's own test,
/// SimplArchiveMobile #20). Everything here is .NET's built-in cryptography: no new dependency.
/// </para>
/// </remarks>
public static class WebPushMessage
{
    private const int RecordSize = 4096;

    /// <summary>The encrypted body: salt, record size, the sender's public key, and one AES-128-GCM record.</summary>
    /// <param name="uaPublic">The device's P-256 public key, uncompressed (65 bytes).</param>
    /// <param name="authSecret">The device's auth secret (16 bytes).</param>
    /// <param name="sender">The sender's one-off key pair; generated when not given (tests pass a fixed one).</param>
    /// <param name="salt">16 random bytes; generated when not given.</param>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman? sender = null, byte[]? salt = null)
    {
        using var ownKey = sender is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var asKey = sender ?? ownKey!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asPublic = Uncompressed(asKey.ExportParameters(false));

        using var device = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(device.PublicKey);

        // RFC 8291 §3.4: the auth secret and both public keys bind the key to this device and this message.
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // One record, the last: the plaintext, then the 0x02 delimiter (RFC 8188 §2), no further padding.
        var record = Concat(plaintext, [0x02]);
        var ciphertext = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, record, ciphertext, tag);
        }

        // RFC 8188 §2.1 header: salt (16) · record size (4, big-endian) · key id length (1) · key id (the sender's key).
        var rs = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rs, RecordSize);
        return Concat(salt, rs, [(byte)asPublic.Length], asPublic, ciphertext, tag);
    }

    /// <summary>
    /// The <c>Authorization</c> value for one push (RFC 8292): a JWT signed with the VAPID private key for the push
    /// service's origin, valid for 12 hours, and the public key it verifies with.
    /// </summary>
    public static string Authorization(string endpoint, string subject, string publicKey, string privateKey, DateTimeOffset now)
    {
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");

        var q = FromBase64Url(publicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = FromBase64Url(privateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] },
        });
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{Base64Url(signature)}, k={publicKey}";
    }

    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Uncompressed(ECParameters key) => Concat([0x04], key.Q.X!, key.Q.Y!);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
