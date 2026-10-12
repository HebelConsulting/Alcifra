using System.Security.Cryptography;

namespace SimplArchive.EndToEndTests;

/// <summary>A phone's Web Push keys (RFC 8291), generated: an uncompressed P-256 public key and a 16-byte auth secret,
/// base64url. The server refuses keys that could not encrypt a push.</summary>
internal static class DeviceKeys
{
    private static readonly ECParameters Point = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).ExportParameters(false);

    public static readonly string P256dh = Base64Url([0x04, .. Point.Q.X!, .. Point.Q.Y!]);

    public static readonly string Auth = Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
