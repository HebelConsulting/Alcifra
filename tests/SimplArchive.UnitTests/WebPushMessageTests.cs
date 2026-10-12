using System.Security.Cryptography;
using System.Text;
using SimplArchive.Api.Notifications;

namespace SimplArchive.UnitTests;

// The phone push's encryption (RFC 8291, aes128gcm) and authorisation (RFC 8292). The device side is written HERE,
// independently of the server's code, from the RFC: what the encryptor produces must open with only the device's
// private key and auth secret and the BODY, since a UnifiedPush distributor hands the phone nothing else.
public class WebPushMessageTests
{
    private static (ECDiffieHellman Device, byte[] Public, byte[] Auth) Device()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        return (key, [0x04, .. p.Q.X!, .. p.Q.Y!], RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>RFC 8291 §3, the receiving side: everything it needs is in the body.</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman device, byte[] uaPublic, byte[] authSecret)
    {
        var salt = body[..16];
        var idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        var record = body[(21 + idLength)..];

        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var ecdh = device.DeriveRawSecretAgreement(sender.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, authSecret, [.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. uaPublic, .. asPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plain = new byte[record.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, record[..^16], record[^16..], plain);
        Assert.Equal(0x02, plain[^1]);   // the last record's delimiter
        return plain[..^1];
    }

    [Fact]
    public void Only_the_device_opens_the_body_and_the_body_alone_is_enough()
    {
        var (device, uaPublic, auth) = Device();
        var message = Encoding.UTF8.GetBytes("{\"id\":\"1\",\"title\":\"Reminder: Contract Müller\"}");

        var body = WebPushMessage.Encrypt(uaPublic, auth, message);

        Assert.Equal(message, Decrypt(body, device, uaPublic, auth));
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
    }

    [Fact]
    public void Another_devices_secret_cannot_open_it()
    {
        var (device, uaPublic, auth) = Device();
        var body = WebPushMessage.Encrypt(uaPublic, auth, "secret"u8.ToArray());

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(body, device, uaPublic, RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void The_authorisation_is_a_jwt_for_the_push_services_origin_signed_by_the_vapid_key()
    {
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = vapid.ExportParameters(true);
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var publicKey = B64([0x04, .. p.Q.X!, .. p.Q.Y!]);

        var value = WebPushMessage.Authorization("https://push.example.test/up/abc", "mailto:ops@example.test", publicKey, B64(p.D!), DateTimeOffset.UnixEpoch.AddYears(56));

        Assert.StartsWith("vapid t=", value, StringComparison.Ordinal);
        var jwt = value["vapid t=".Length..value.IndexOf(',', StringComparison.Ordinal)];
        var parts = jwt.Split('.');
        var claims = Encoding.UTF8.GetString(WebPushMessage.FromBase64Url(parts[1]));
        Assert.Contains("\"aud\":\"https://push.example.test\"", claims, StringComparison.Ordinal);
        Assert.True(vapid.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), WebPushMessage.FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.EndsWith($"k={publicKey}", value, StringComparison.Ordinal);
    }
}
