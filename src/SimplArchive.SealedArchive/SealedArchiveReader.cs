using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SimplArchive.SealedArchive.Errors;

namespace SimplArchive.SealedArchive;

/// <summary>
/// Reads a <c>.simplarchive</c> (ADR 0961): <see cref="ReadCover"/> verifies the cover with no key; <see cref="OpenAsync"/>
/// opens the payload with the reader's key. Every refusal is a named <see cref="SealedArchiveException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust is the reader's, not the archive's.</b> The signature is checked against the signing keys the caller
/// trusts (an installation's published key history, kept from when it was online), never against the certificate the
/// archive carries: that one says who CLAIMS to have signed it, and anybody can embed a certificate.
/// </para>
/// <para>
/// <b>Order of checks:</b> the cover's signature, then its signer, then the key envelope's hash, then (with the key) the
/// table of contents' hash, then each file's tag and hash as it is read. A file is never handed out before its check.
/// </para>
/// </remarks>
public static class SealedArchiveReader
{
    /// <summary>The verified cover, without a key: the signature holds and its signer is one of <paramref name="trustedSigningKeys"/>.</summary>
    public static ArchiveCover ReadCover(Stream archive, IReadOnlyCollection<X509Certificate2> trustedSigningKeys)
    {
        using var zip = OpenZip(archive);
        return VerifiedCover(zip, trustedSigningKeys);
    }

    /// <summary>
    /// Opens the payload: verifies the cover, opens the key envelope through <paramref name="openKeyEnvelope"/> (the
    /// reader's card, or the recovery key), and checks the table of contents against the cover.
    /// </summary>
    /// <param name="archive">A seekable stream over the archive; owned by the returned archive from here on.</param>
    /// <param name="openKeyEnvelope">Opens a CMS envelope addressed to the reader's certificate and returns its content.</param>
    public static async Task<OpenedSealedArchive> OpenAsync(
        Stream archive,
        IReadOnlyCollection<X509Certificate2> trustedSigningKeys,
        Func<byte[], CancellationToken, Task<byte[]>> openKeyEnvelope,
        CancellationToken cancellationToken = default)
    {
        var zip = OpenZip(archive);
        try
        {
            var cover = VerifiedCover(zip, trustedSigningKeys);
            var envelope = Read(zip, SealedArchiveLayout.KeyEnvelope) ?? throw new ArchiveFormatException("it has no key envelope");
            if (PayloadCipher.Sha256(envelope) != cover.KeyEnvelopeSha256)
            {
                throw new ArchiveCoverTamperedException("the key envelope is not the one the cover names");
            }

            byte[] key;
            try
            {
                key = await openKeyEnvelope(envelope, cancellationToken);
            }
            catch (CryptographicException e)
            {
                throw new ArchiveNotForThisKeyException(e);
            }

            if (key.Length != 32)
            {
                throw new ArchiveNotForThisKeyException();
            }

            var sealedContents = Read(zip, SealedArchiveLayout.Contents) ?? throw new ArchiveFormatException("it has no table of contents");
            byte[] contents;
            try
            {
                contents = PayloadCipher.Open(key, SealedArchiveLayout.Contents, sealedContents);
            }
            catch (CryptographicException e)
            {
                throw new ArchiveBlobTamperedException(SealedArchiveLayout.Contents, e);
            }

            if (PayloadCipher.Sha256(contents) != cover.ContentsSha256)
            {
                throw new ArchiveBlobTamperedException(SealedArchiveLayout.Contents);
            }

            var entries = JsonSerializer.Deserialize<List<ContentsEntry>>(contents, ArchiveCover.Json)
                ?? throw new ArchiveFormatException("its table of contents is empty");
            return new OpenedSealedArchive(zip, key, cover, entries);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    private static ZipArchive OpenZip(Stream archive)
    {
        try
        {
            return new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException e)
        {
            throw new ArchiveFormatException("it is not a zip file", e);
        }
    }

    private static ArchiveCover VerifiedCover(ZipArchive zip, IReadOnlyCollection<X509Certificate2> trustedSigningKeys)
    {
        var coverBytes = Read(zip, SealedArchiveLayout.Cover) ?? throw new ArchiveFormatException("it has no cover");
        var signature = Read(zip, SealedArchiveLayout.CoverSignature) ?? throw new ArchiveCoverTamperedException("it carries no signature");

        var signed = new SignedCms(new ContentInfo(coverBytes), detached: true);
        try
        {
            signed.Decode(signature);
            signed.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException e)
        {
            throw new ArchiveCoverTamperedException("the signature does not match the cover", e);
        }

        // The installation's signature is the first signer; a countersignature (the exporter's card on release, #1696)
        // rides on that signer and is not what makes the archive the installation's.
        if (signed.SignerInfos.Count == 0 || signed.SignerInfos[0].Certificate is not { } signer)
        {
            throw new ArchiveCoverTamperedException("the signature names no signing certificate");
        }

        var signerKey = signer.PublicKey.ExportSubjectPublicKeyInfo();
        if (!trustedSigningKeys.Any(t => t.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(signerKey)))
        {
            throw new ArchiveForeignSignatureException(SealedArchiveWriter.Thumbprint(signer));
        }

        var cover = ArchiveCover.FromBytes(coverBytes) ?? throw new ArchiveFormatException("its cover is not readable");
        if (cover.Format is not (SealedArchiveLayout.Format or SealedArchiveLayout.LegacyFormat))
        {
            throw new ArchiveFormatException($"its cover says '{cover.Format}'");
        }

        if (cover.FormatVersion > SealedArchiveLayout.FormatVersion)
        {
            throw new ArchiveFormatException($"it is format version {cover.FormatVersion}, newer than this reader's {SealedArchiveLayout.FormatVersion}");
        }

        return cover;
    }

    internal static byte[]? Read(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// An opened <c>.simplarchive</c>: its verified cover, and its payload files by their export-layout paths, each checked
/// against its tag and its hash before it is handed out (ADR 0961).
/// </summary>
public sealed class OpenedSealedArchive : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly byte[] _key;
    private readonly Dictionary<string, ContentsEntry> _entries;
    private readonly Lock _gate = new();

    internal OpenedSealedArchive(ZipArchive zip, byte[] key, ArchiveCover cover, IReadOnlyList<ContentsEntry> entries)
    {
        _zip = zip;
        _key = key;
        Cover = cover;
        _entries = entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
    }

    public ArchiveCover Cover { get; }

    /// <summary>The payload's paths in the export layout (ADR 0347).</summary>
    public IReadOnlyCollection<string> Paths => _entries.Keys;

    /// <summary>A payload file, or null when the payload has no such path; refused by name when it does not match.</summary>
    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(path, out var listed))
        {
            return null;
        }

        byte[]? sealedBytes;
        lock (_gate)
        {
            // A ZipArchive is not safe for concurrent reads; the responder may ask for two files at once.
            sealedBytes = SealedArchiveReader.Read(_zip, listed.Entry);
        }

        if (sealedBytes is null)
        {
            throw new ArchiveBlobTamperedException(path);
        }

        byte[] plaintext;
        try
        {
            plaintext = PayloadCipher.Open(_key, listed.Entry, sealedBytes);
        }
        catch (CryptographicException e)
        {
            throw new ArchiveBlobTamperedException(path, e);
        }

        return PayloadCipher.Sha256(plaintext) == listed.Sha256 ? plaintext : throw new ArchiveBlobTamperedException(path);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        _zip.Dispose();
    }
}
