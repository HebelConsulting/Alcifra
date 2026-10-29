using System.Security.Cryptography;
using System.Text;

namespace Alcifra.SealedArchive;

/// <summary>
/// One payload entry, sealed with the archive's payload key: AES-256-GCM, a fresh nonce per entry, and the entry's
/// own NAME as associated data, so an entry moved to another name fails its tag (ADR 0961).
/// Stored as <c>nonce (12) ‖ tag (16) ‖ ciphertext</c>.
/// </summary>
internal static class PayloadCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Seal(byte[] key, string entry, ReadOnlySpan<byte> plaintext)
    {
        var sealedBytes = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(entry));
        return sealedBytes;
    }

    /// <summary>The plaintext, or a <see cref="CryptographicException"/> when the entry was changed or renamed.</summary>
    public static byte[] Open(byte[] key, string entry, byte[] sealedBytes)
    {
        if (sealedBytes.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The sealed entry is shorter than its nonce and tag.");
        }

        var plaintext = new byte[sealedBytes.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), plaintext,
            Encoding.UTF8.GetBytes(entry));
        return plaintext;
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
