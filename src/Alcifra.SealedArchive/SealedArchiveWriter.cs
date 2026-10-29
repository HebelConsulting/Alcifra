using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SimplArchive.Messenger.Client;

namespace Alcifra.SealedArchive;

/// <summary>One certificate the payload key is sealed to, and how the cover names it.</summary>
public sealed record SealedArchiveRecipient(string Label, ArchiveRecipientKind Kind, X509Certificate2 Certificate);

/// <summary>
/// Writes a <c>.simplarchive</c> (ADR 0961): payload files are added one at a time and sealed as they arrive, then
/// <see cref="CompleteAsync"/> writes the sealed table of contents, the key envelope, the cover and the installation's
/// signature over it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One payload key, sealed once to every recipient</b> by <see cref="ConversationKeys.SealTo"/> (RSA key transport or
/// elliptic-curve key agreement), the same sealer the Messenger and the hardware-key test use: never a second CMS writer.
/// Each payload file is its own AES-256-GCM entry, so a reader opens one file at a time without unpacking the rest.
/// </para>
/// <para>
/// <b>The recovery certificate is not optional</b> (ADR 0960): an archive without one is refused here as well as by
/// the caller, because an archive with no recovery path fails silently years later.
/// </para>
/// </remarks>
public sealed class SealedArchiveWriter : IAsyncDisposable
{
    private readonly ZipArchive _zip;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly List<ContentsEntry> _contents = [];
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private bool _completed;

    /// <param name="output">Where the archive is written; left open.</param>
    public SealedArchiveWriter(Stream output) => _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

    /// <summary>Seals one payload file under its path in the export layout (ADR 0347). A path is added once.</summary>
    public async Task AddAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (!_paths.Add(path))
        {
            throw new ArgumentException($"The payload already holds '{path}'.", nameof(path));
        }

        var entry = SealedArchiveLayout.Entry(_contents.Count);
        _contents.Add(new ContentsEntry(path, entry, PayloadCipher.Sha256(content.Span), content.Length));
        await WriteEntryAsync(entry, PayloadCipher.Seal(_key, entry, content.Span), cancellationToken);
    }

    /// <summary>The payload paths added so far.</summary>
    public IReadOnlyCollection<string> Paths => _paths;

    /// <summary>
    /// Finishes the archive: the table of contents, the key envelope to <paramref name="recipients"/>, the cover, and
    /// the signature by <paramref name="signingCertificate"/> (with its private key) with the certificate embedded.
    /// </summary>
    /// <param name="signingKeys">The installation's published signing keys, newest first; embedded for the record.</param>
    public async Task<ArchiveCover> CompleteAsync(
        string name,
        DateTimeOffset exportedAt,
        ArchiveSource source,
        IReadOnlyList<SealedArchiveRecipient> recipients,
        X509Certificate2 signingCertificate,
        IReadOnlyList<X509Certificate2> signingKeys,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (!recipients.Any(r => r.Kind == ArchiveRecipientKind.Recovery))
        {
            throw new ArgumentException("Every archive is sealed to the tenant's recovery certificate (ADR 0960).", nameof(recipients));
        }

        if (!signingCertificate.HasPrivateKey)
        {
            throw new ArgumentException("The signing certificate carries no private key.", nameof(signingCertificate));
        }

        var contents = JsonSerializer.SerializeToUtf8Bytes(_contents, ArchiveCover.Json);
        await WriteEntryAsync(SealedArchiveLayout.Contents, PayloadCipher.Seal(_key, SealedArchiveLayout.Contents, contents), cancellationToken);

        // Several certificates of one person are several recipients of ONE envelope (ADR 0842): no precedence.
        var envelope = ConversationKeys.SealTo(_key, [.. recipients.Select(r => r.Certificate)]);
        await WriteEntryAsync(SealedArchiveLayout.KeyEnvelope, envelope, cancellationToken);

        var keys = Encoding.ASCII.GetBytes(string.Concat(signingKeys.Select(k => $"{k.ExportCertificatePem()}\n")));
        await WriteEntryAsync(SealedArchiveLayout.SigningKeys, keys, cancellationToken);

        var cover = new ArchiveCover(
            SealedArchiveLayout.Format,
            SealedArchiveLayout.FormatVersion,
            name,
            exportedAt,
            source,
            [.. recipients.Select(r => new ArchiveRecipientLabel(r.Label, r.Kind, Thumbprint(r.Certificate), r.Certificate.NotAfter.ToUniversalTime()))],
            PayloadCipher.Sha256(contents),
            PayloadCipher.Sha256(envelope),
            PayloadCipher.Sha256(keys));
        var coverBytes = cover.ToBytes();
        await WriteEntryAsync(SealedArchiveLayout.Cover, coverBytes, cancellationToken);
        await WriteEntryAsync(SealedArchiveLayout.CoverSignature, Sign(coverBytes, signingCertificate, exportedAt), cancellationToken);

        _completed = true;
        CryptographicOperations.ZeroMemory(_key);
        _zip.Dispose();
        return cover;
    }

    /// <summary>The SHA-256 thumbprint a cover names a certificate by (ADR 0793's form: upper-case hex).</summary>
    public static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    // Detached CMS SignedData over the cover's exact bytes; the signing certificate travels inside.
    private static byte[] Sign(byte[] cover, X509Certificate2 signingCertificate, DateTimeOffset signedAt)
    {
        var signed = new SignedCms(new ContentInfo(cover), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signingCertificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signedAt.UtcDateTime));
        signed.ComputeSignature(signer);
        return signed.Encode();
    }

    private async Task WriteEntryAsync(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            // An archive never completed is no archive: the caller discards the stream.
            _completed = true;
            CryptographicOperations.ZeroMemory(_key);
            _zip.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
