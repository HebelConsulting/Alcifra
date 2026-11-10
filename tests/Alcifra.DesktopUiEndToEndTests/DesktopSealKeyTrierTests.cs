using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.UiEndToEndTests;

// Which of my keys opens it (#2291): the Inspect dialog's key try over a local sealed file, with the shared KeyTry the CLI's
// --try-open uses. A key in this computer's store that is one of the user's enrolled keys opens it; a picked key file is said
// to be named or not. The card is not reached here: a key in the store opens first, and the file path never asks it.
[Collection(UiCollection.Name)]
public class DesktopSealKeyTrierTests : IDisposable
{
    private readonly Func<X509Certificate2Collection> _keys = EnvelopeOpener.Keys;
    private readonly Func<CancellationToken, Task<EnrolledKeysAnswer>> _signedIn = EnvelopeOpener.SignedIn;

    public void Dispose()
    {
        EnvelopeOpener.Keys = _keys;
        EnvelopeOpener.SignedIn = _signedIn;
    }

    [Fact]
    public async Task An_enrolled_key_in_the_store_opens_it_and_a_picked_key_file_is_named_or_not()
    {
        var mine = NewCertificate("Anna Muster");
        var stranger = NewCertificate("Somebody Else");
        var sealedFile = SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [X509CertificateLoader.LoadCertificate(mine.RawData)]);
        EnvelopeOpener.Keys = () => [mine];
        EnvelopeOpener.SignedIn = _ => Task.FromResult(new EnrolledKeysAnswer([EnrolledKey.Of(mine, "Office key")]));

        var inspection = new SealInspectionViewModel(
            Alcifra.ApiClient.Inspection.SealReport.Of(Alcifra.ApiClient.Inspection.SealedInspector.Inspect(sealedFile), []),
            mayReseal: false, SealKeyTrier.Of(sealedFile));
        Assert.True(inspection.CanTryKeys);

        await inspection.TryMineAsync();
        Assert.True(inspection.KeyOpened);
        Assert.Equal(string.Format(Strings.Get("SealTryOpened"), mine.Subject), inspection.KeyVerdict);

        await inspection.TryKeyAsync(stranger);
        Assert.False(inspection.KeyOpened);
        Assert.Equal(string.Format(Strings.Get("SealTryNotAddressed"), stranger.Subject), inspection.KeyVerdict);

        // A key file as a PEM holding its private key: loaded the way the dialog loads it, and it opens.
        using var fromPem = Alcifra.HardwareKeys.KeyTry.LoadKeyFile(
            System.Text.Encoding.ASCII.GetBytes($"{mine.ExportCertificatePem()}\n{mine.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem()}"), null);
        await inspection.TryKeyAsync(fromPem);
        Assert.True(inspection.KeyOpened);
    }

    private static X509Certificate2 NewCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
