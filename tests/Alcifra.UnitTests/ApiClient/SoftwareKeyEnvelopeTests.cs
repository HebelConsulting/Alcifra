using System.Security.Cryptography.X509Certificates;
using Alcifra.ApiClient.Inspection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit.Cryptography;
using static Alcifra.UnitTests.SealedArchive.SealedArchiveTestKeys;

namespace Alcifra.UnitTests.ApiClient;

// The software-key opener the desktop and `alcifra inspect --try-open` share (#2007 slice 3), and try-open itself.
public class SoftwareKeyEnvelopeTests
{
    private static readonly X509Certificate2 Anna = Rsa("Anna Muster");
    private static readonly X509Certificate2 Tom = Ec("Tom Kern");
    private static readonly X509Certificate2 Stranger = Rsa("Somebody Else");

    private static byte[] Sealed() => Alcifra.ApiClient.SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [PublicOnly(Anna), PublicOnly(Tom)]);

    [Fact]
    public void An_RSA_addressee_opens_it_as_a_document_and_raw_and_anybody_else_declines()
    {
        Assert.Equal("minutes"u8.ToArray(), Alcifra.ApiClient.SoftwareKeyEnvelope.TryOpen(new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(Sealed())), [Anna])!.Value.Bytes);
        Assert.NotNull(Alcifra.ApiClient.SoftwareKeyEnvelope.TryOpenRaw(Sealed(), [Anna]));   // raw: no MIME framing needed
        Assert.Null(Alcifra.ApiClient.SoftwareKeyEnvelope.TryOpenRaw(Sealed(), [Stranger]));
    }

    // #2312: a decrypted sealed document was written out as ".p7m". The name sealed with it comes out with the content, and
    // the file is named after what it decrypted to.
    [Fact]
    public void The_file_name_sealed_with_a_document_comes_out_with_it()
    {
        var named = Alcifra.ApiClient.SealedDocument.Seal("%PDF"u8.ToArray(), "application/pdf", [PublicOnly(Anna)], "contract.pdf");

        var opened = Alcifra.ApiClient.SoftwareKeyEnvelope.TryOpenNamed(new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(named)), [Anna])!.Value;

        Assert.Equal(("application/pdf", "contract.pdf"), (opened.ContentType, opened.FileName));
        Assert.Null(Alcifra.ApiClient.SoftwareKeyEnvelope.TryOpenNamed(new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(Sealed())), [Anna])!.Value.FileName);
    }

    [Theory]
    [InlineData("Board pack.p7m", "contract.pdf", "application/pdf", "contract.pdf")]      // the sealed name, a hidden one too
    [InlineData("Board pack.p7m", null, "application/pdf", "Board pack.pdf")]             // none sealed: the type names it
    [InlineData("Board pack.p7m", "Board pack", "application/pdf", "Board pack.pdf")]     // a re-seal carries the bare name
    [InlineData("Board pack v2.p7m", null, "application/x-unknown", "Board pack v2")]     // an unknown type: no extension, never .p7m
    [InlineData("scan.tif", "scan.tif", "image/tiff", "scan.tif")]                        // a strict tenant's own envelope
    public void A_decrypted_document_is_named_after_what_it_is(string filedAs, string? sealedName, string contentType, string expected) =>
        Assert.Equal(expected, Alcifra.ApiClient.SealedDocument.OpenedFileName(filedAs, sealedName, contentType));

    [Theory]
    [InlineData("rsa")]
    [InlineData("ecdh")]
    public async Task Try_open_with_a_key_file_says_which_key_opens_it(string kind)
    {
        var key = kind == "rsa" ? Anna : Tom;
        var file = Path.Combine(Directory.CreateTempSubdirectory("try-open-").FullName, "key.pem");
        await File.WriteAllTextAsync(file, key.ExportCertificatePem() + "\n" + (kind == "rsa" ? key.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem() : key.GetECDiffieHellmanPrivateKey()!.ExportPkcs8PrivateKeyPem()));

        var cms = Sealed();
        var opened = await Alcifra.Cli.Commands.InspectSurface.TryOpenAsync(cms, SealedInspector.Inspect(cms).Recipients, file, NullLogger.Instance);

        Assert.Contains(key.Subject, opened.OpenedBy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Try_open_with_somebody_elses_key_says_it_is_not_addressed_to_it()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("try-open-").FullName, "key.pem");
        await File.WriteAllTextAsync(file, Stranger.ExportCertificatePem() + "\n" + Stranger.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        var cms = Sealed();
        var opened = await Alcifra.Cli.Commands.InspectSurface.TryOpenAsync(cms, SealedInspector.Inspect(cms).Recipients, file, NullLogger.Instance);

        Assert.Null(opened.OpenedBy);
        Assert.Contains("not addressed", opened.Why, StringComparison.Ordinal);
    }
}
