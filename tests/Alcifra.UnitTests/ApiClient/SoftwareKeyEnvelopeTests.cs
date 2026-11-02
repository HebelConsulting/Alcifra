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
