using System.Text;
using Alcifra.ApiClient.Offline;
using Alcifra.SealedArchive;
using Alcifra.SealedArchive.Errors;
using static Alcifra.UnitTests.SealedArchive.SealedArchiveTestKeys;

namespace Alcifra.UnitTests.SealedArchive;

// The .simplarchive container (ADR 0961, #1694): it opens with the recovery key alone (ADR 0960), with a member's EC
// card key, and every way it can be wrong is refused BY NAME: a tampered cover, a tampered file, a foreign signature,
// a key it was not sealed to.
public class SealedArchiveTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Installation = Rsa("Alcifra archive signing");
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Recovery = Rsa("Acme recovery");
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Member = Ec("Anna card");

    private static IReadOnlyList<SealedArchiveRecipient> Recipients =>
    [
        new("Anna Example", ArchiveRecipientKind.Exporter, PublicOnly(Member)),
        new("Recovery certificate", ArchiveRecipientKind.Recovery, PublicOnly(Recovery)),
    ];

    private static Task<OpenedSealedArchive> OpenAsync(byte[] archive, System.Security.Cryptography.X509Certificates.X509Certificate2 key,
        System.Security.Cryptography.X509Certificates.X509Certificate2? trusted = null) =>
        SealedArchiveReader.OpenAsync(new MemoryStream(archive), [PublicOnly(trusted ?? Installation)], OpenerFor(key));

    [Fact]
    public async Task It_opens_with_the_recovery_key_alone_and_with_the_members_EC_key_and_serves_what_was_sealed()
    {
        var archive = await WriteAsync(Installation, Recipients);

        foreach (var key in new[] { Recovery, Member })
        {
            using var opened = await OpenAsync(archive, key);
            Assert.Equal("Road copy", opened.Cover.Name);
            Assert.Equal(["blobs/abc", "manifest.json"], opened.Paths.Order());
            Assert.Equal("the content", Encoding.UTF8.GetString(opened.Read("blobs/abc")!));
            Assert.Null(opened.Read("blobs/not-there"));

            // the responder's storage reads the same payload
            Assert.Equal("{\"formatVersion\":2}", Encoding.UTF8.GetString((await new SealedArchiveStorage(opened).ReadAsync("manifest.json"))!));
        }
    }

    [Fact]
    public async Task The_cover_reads_without_any_key_and_names_who_can_open_it()
    {
        var archive = await WriteAsync(Installation, Recipients);

        var cover = SealedArchiveReader.ReadCover(new MemoryStream(archive), [PublicOnly(Installation)]);

        Assert.Equal(("alcifra", 1, "Acme", "https://archive.example/api"), (cover.Format, cover.FormatVersion, cover.Source.TenantName, cover.Source.ApiRoot));
        Assert.Equal([(ArchiveRecipientKind.Exporter, "Anna Example"), (ArchiveRecipientKind.Recovery, "Recovery certificate")],
            cover.Recipients.Select(r => (r.Kind, r.Label)));
        Assert.Equal(SealedArchiveWriter.Thumbprint(Recovery), cover.Recipients[1].Thumbprint);
        // nothing of the content is readable from the plain parts
        Assert.DoesNotContain("the content", Encoding.UTF8.GetString(archive), StringComparison.Ordinal);
    }

    // Rename phase 4b (#1117): an archive written before it names the format "simplarchive" and still opens until v0.57.0;
    // a cover naming anything else is still refused by name. The cover is re-signed, so only the format differs.
    [Theory]
    [InlineData("simplarchive", true)]
    [InlineData("somethingelse", false)]
    public async Task A_cover_naming_the_format_before_the_rename_still_reads(string format, bool reads)
    {
        var archive = await WriteAsync(Installation, Recipients);
        byte[]? cover = null;
        var renamed = Rewrite(archive, SealedArchiveLayout.Cover, bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"alcifra\"", text, StringComparison.Ordinal);
            cover = Encoding.UTF8.GetBytes(text.Replace("\"alcifra\"", $"\"{format}\"", StringComparison.Ordinal));
            return cover;
        });
        var resigned = Rewrite(renamed, SealedArchiveLayout.CoverSignature, _ =>
        {
            var signed = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(cover!), detached: true);
            signed.ComputeSignature(new System.Security.Cryptography.Pkcs.CmsSigner(Installation)
            {
                DigestAlgorithm = new System.Security.Cryptography.Oid("2.16.840.1.101.3.4.2.1"),
                IncludeOption = System.Security.Cryptography.X509Certificates.X509IncludeOption.EndCertOnly,
            });
            return signed.Encode();
        });

        if (reads)
        {
            Assert.Equal(format, SealedArchiveReader.ReadCover(new MemoryStream(resigned), [PublicOnly(Installation)]).Format);
        }
        else
        {
            Assert.Throws<ArchiveFormatException>(() => SealedArchiveReader.ReadCover(new MemoryStream(resigned), [PublicOnly(Installation)]));
        }
    }

    [Fact]
    public async Task A_tampered_cover_is_refused_by_name()
    {
        var archive = await WriteAsync(Installation, Recipients);
        var renamed = Rewrite(archive, SealedArchiveLayout.Cover, bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("Road copy", "Road copZ", StringComparison.Ordinal)));

        Assert.Throws<ArchiveCoverTamperedException>(() => SealedArchiveReader.ReadCover(new MemoryStream(renamed), [PublicOnly(Installation)]));
        await Assert.ThrowsAsync<ArchiveCoverTamperedException>(() => OpenAsync(renamed, Recovery));
    }

    [Fact]
    public async Task A_missing_signature_is_a_tampered_cover()
    {
        var archive = await WriteAsync(Installation, Recipients);
        var unsigned = Rewrite(archive, SealedArchiveLayout.CoverSignature, _ => []);

        await Assert.ThrowsAsync<ArchiveCoverTamperedException>(() => OpenAsync(unsigned, Recovery));
    }

    [Fact]
    public async Task A_tampered_blob_is_refused_by_name_and_path()
    {
        var archive = await WriteAsync(Installation, Recipients);
        // the entry that holds blobs/abc: entries are numbered in the order files were added
        var tampered = Rewrite(archive, SealedArchiveLayout.Entry(1), Flip);

        using var opened = await OpenAsync(tampered, Recovery);
        var refusal = Assert.Throws<ArchiveBlobTamperedException>(() => opened.Read("blobs/abc"));
        Assert.Equal("blobs/abc", refusal.Path);
        Assert.NotNull(opened.Read("manifest.json"));   // the untouched file still reads
    }

    [Fact]
    public async Task Swapping_two_sealed_files_is_refused()
    {
        var archive = await WriteAsync(Installation, Recipients);
        var first = Read(archive, SealedArchiveLayout.Entry(0));
        var swapped = Rewrite(archive, SealedArchiveLayout.Entry(1), _ => first);

        using var opened = await OpenAsync(swapped, Recovery);
        Assert.Throws<ArchiveBlobTamperedException>(() => opened.Read("blobs/abc"));
    }

    [Fact]
    public async Task A_tampered_table_of_contents_is_refused()
    {
        var archive = await WriteAsync(Installation, Recipients);
        var tampered = Rewrite(archive, SealedArchiveLayout.Contents, Flip);

        await Assert.ThrowsAsync<ArchiveBlobTamperedException>(() => OpenAsync(tampered, Recovery));
    }

    [Fact]
    public async Task A_replaced_key_envelope_is_refused_as_a_tampered_cover()
    {
        var archive = await WriteAsync(Installation, Recipients);
        var other = await WriteAsync(Installation, Recipients);
        var replaced = Rewrite(archive, SealedArchiveLayout.KeyEnvelope, _ => Read(other, SealedArchiveLayout.KeyEnvelope));

        await Assert.ThrowsAsync<ArchiveCoverTamperedException>(() => OpenAsync(replaced, Recovery));
    }

    [Fact]
    public async Task A_foreign_signature_is_refused_by_name_even_when_it_is_valid()
    {
        var foreign = Rsa("Somebody else's installation");
        var archive = await WriteAsync(foreign, Recipients);

        var refusal = Assert.Throws<ArchiveForeignSignatureException>(() => SealedArchiveReader.ReadCover(new MemoryStream(archive), [PublicOnly(Installation)]));
        Assert.Equal(SealedArchiveWriter.Thumbprint(foreign), refusal.Thumbprint);
        await Assert.ThrowsAsync<ArchiveForeignSignatureException>(() => OpenAsync(archive, Recovery));
    }

    [Fact]
    public async Task A_past_signing_key_still_verifies_when_the_reader_trusts_the_published_history()
    {
        var retired = Rsa("Alcifra archive signing (retired)");
        var archive = await WriteAsync(retired, Recipients);

        var cover = SealedArchiveReader.ReadCover(new MemoryStream(archive), [PublicOnly(Installation), PublicOnly(retired)]);

        Assert.Equal("Road copy", cover.Name);
    }

    [Fact]
    public async Task A_key_it_was_not_sealed_to_is_refused_by_name()
    {
        var archive = await WriteAsync(Installation, Recipients);

        await Assert.ThrowsAsync<ArchiveNotForThisKeyException>(() => OpenAsync(archive, Rsa("A stranger")));
    }

    [Fact]
    public async Task An_archive_without_the_recovery_certificate_is_never_written()
    {
        using var output = new MemoryStream();
        await using var writer = new SealedArchiveWriter(output);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.CompleteAsync("x", DateTimeOffset.UtcNow, new ArchiveSource("r", Guid.Empty, "t", "v"),
            [new SealedArchiveRecipient("Anna", ArchiveRecipientKind.Exporter, PublicOnly(Member))], Installation, []));
    }

    [Fact]
    public async Task Something_that_is_not_an_archive_is_refused_by_name()
    {
        await Assert.ThrowsAsync<ArchiveFormatException>(() => OpenAsync("not a zip"u8.ToArray(), Recovery));
    }

    private static byte[] Read(byte[] archive, string entry)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(archive));
        using var stream = zip.GetEntry(entry)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
