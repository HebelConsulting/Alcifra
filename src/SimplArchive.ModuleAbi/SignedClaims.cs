using System.Security.Cryptography;
using System.Text;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// The one signature every vendor licence carries (ECDsa P-256 / SHA-256 over a licence's fixed payload string,
/// base64): <see cref="TenantLicense"/> and <see cref="MobileLicense"/> sign and verify through it, so the two cannot
/// drift (core ADR 0954).
/// </summary>
internal static class SignedClaims
{
    public static string Sign(ECDsa privateKey, string payload) =>
        Convert.ToBase64String(privateKey.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

    public static bool Verify(ECDsa publicKey, string payload, string signature)
    {
        try
        {
            return publicKey.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;   // a signature that is not base64 at all is a failed verification, not a crash
        }
    }
}
