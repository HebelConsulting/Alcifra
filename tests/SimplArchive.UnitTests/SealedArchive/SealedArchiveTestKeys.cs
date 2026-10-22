extern alias BouncyCastleCryptography;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.SealedArchive;

namespace SimplArchive.UnitTests.SealedArchive;

/// <summary>
/// Software keys standing in for a card, the recovery key in its safe, and the installation's signing key, plus the
/// one opener every test uses: BouncyCastle's CMS, which opens RSA key transport and EC key agreement alike, as the
/// desktop's card opener does (ADR 0830).
/// </summary>
internal static class SealedArchiveTestKeys
{
    public static X509Certificate2 Rsa(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 Ec(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyAgreement, critical: true));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var made = request.Create(request.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);
        // An EC card key is a key-AGREEMENT key: attached as ECDiffieHellman, which is what opening an envelope needs.
        using var agreement = ECDiffieHellman.Create(key.ExportParameters(includePrivateParameters: true));
        using var withKey = made.CopyWithPrivateKey(agreement);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>The public half alone, as an uploaded certificate is.</summary>
    public static X509Certificate2 PublicOnly(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);

    /// <summary>Opens a key envelope with <paramref name="holder"/>'s private key, RSA or EC.</summary>
    public static Func<byte[], CancellationToken, Task<byte[]>> OpenerFor(X509Certificate2 holder) => (envelope, _) =>
    {
        var bcCertificate = new BouncyCastleCryptography::Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(holder.RawData);
        var privateKey = BouncyCastleCryptography::Org.BouncyCastle.Security.PrivateKeyFactory.CreateKey(
            holder.GetRSAPrivateKey()?.ExportPkcs8PrivateKey() ?? holder.GetECDiffieHellmanPrivateKey()!.ExportPkcs8PrivateKey());
        var enveloped = new BouncyCastleCryptography::Org.BouncyCastle.Cms.CmsEnvelopedData(envelope);
        var recipient = enveloped.GetRecipientInfos().GetFirstRecipient(
            new BouncyCastleCryptography::Org.BouncyCastle.Cms.RecipientID { Issuer = bcCertificate.IssuerDN, SerialNumber = bcCertificate.SerialNumber });
        return recipient is null
            ? throw new CryptographicException("The envelope is not addressed to this certificate.")
            : Task.FromResult(recipient.GetContent(privateKey));
    };

    /// <summary>Writes a small archive with two payload files, sealed to <paramref name="recipients"/>.</summary>
    public static async Task<byte[]> WriteAsync(X509Certificate2 signer, IReadOnlyList<SealedArchiveRecipient> recipients, string name = "Road copy")
    {
        using var output = new MemoryStream();
        await using (var writer = new SealedArchiveWriter(output))
        {
            await writer.AddAsync("manifest.json", "{\"formatVersion\":2}"u8.ToArray());
            await writer.AddAsync("blobs/abc", "the content"u8.ToArray());
            await writer.CompleteAsync(name, DateTimeOffset.Parse("2026-10-22T10:00:00Z"),
                new ArchiveSource("https://archive.example/api", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme", "1.0.0"),
                recipients, signer, [PublicOnly(signer)]);
        }

        return output.ToArray();
    }

    /// <summary>The archive with one entry's bytes replaced.</summary>
    public static byte[] Rewrite(byte[] archive, string entry, Func<byte[], byte[]> change)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                using var stream = e.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                entries.Add((e.FullName, e.FullName == entry ? change(buffer.ToArray()) : buffer.ToArray()));
            }
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    public static byte[] Flip(byte[] bytes)
    {
        var copy = bytes.ToArray();
        copy[^1] ^= 0x01;
        return copy;
    }
}
