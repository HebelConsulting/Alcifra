using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.DesktopClient.Services;
using Alcifra.TestSupport;

namespace Alcifra.UiEndToEndTests;

// The token's attestation of a chosen certificate's key (#1790, ADR 0963), found among the certificates a PKCS#11 module
// exposes: the slot's attestation is the OTHER certificate with the same public key, and the token's attestation
// certificate is the one that issued it. By key, never by label, and nothing at all for a key the token did not attest.
public class DesktopCardAttestationTests
{
    private static CardCertificates.Found On(string token, string label, string pem) =>
        new(token, "36771502", label, X509Certificate2.CreateFromPem(pem));

    [Fact]
    public void The_attestation_of_the_chosen_key_and_the_token_certificate_that_issued_it_are_found()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var key = hierarchy.NewKey("holder");
        var other = hierarchy.NewKey("signing");
        var chosen = On("PIV", "X.509 Certificate for Key Management", key.CertificatePem);
        var all = new List<CardCertificates.Found>
        {
            On("PIV", "X.509 Certificate for Digital Signature", other.CertificatePem),
            chosen,
            On("PIV", "X.509 Certificate for PIV Attestation 9c", other.AttestationPem),
            On("PIV", "X.509 Certificate for PIV Attestation 9d", key.AttestationPem),
            On("PIV", "X.509 Certificate for PIV Attestation", hierarchy.Device.ExportCertificatePem()),
        };

        var attestation = CardCertificates.AttestationFor(all, chosen)!;

        Assert.Equal("piv", attestation.Format);
        Assert.Equal("9d", attestation.Slot);
        Assert.Equal(key.AttestationPem.Trim(), attestation.AttestationCertificatePem.Trim());
        Assert.Equal(hierarchy.Device.ExportCertificatePem().Trim(), attestation.IntermediateCertificatePem.Trim());
    }

    [Fact]
    public void A_key_the_token_did_not_attest_has_no_attestation()
    {
        // An imported key, or a module that exposes no attestation objects: nothing is invented.
        using var hierarchy = TestAttestationHierarchy.Direct();
        var key = hierarchy.NewKey("imported");
        var chosen = On("PIV", "X.509 Certificate for Key Management", key.CertificatePem);

        Assert.Null(CardCertificates.AttestationFor([chosen, On("PIV", "X.509 Certificate for PIV Attestation", hierarchy.Device.ExportCertificatePem())], chosen));
    }
    // The picker offers what may be REGISTERED: the token's attestation certificate (F9) and the slot attestation are
    // evidence, not reader certificates — measured on a real key, which lists all three per token. AttestationFor still
    // sees them, because it is handed the full list.
    [Fact]
    public void The_picker_offers_the_reader_certificate_and_not_the_attestation_evidence()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var key = hierarchy.NewKey("holder");
        var chosen = On("PIV", "X.509 Certificate for Key Management", key.CertificatePem);
        var all = new List<CardCertificates.Found>
        {
            chosen,
            On("PIV", "X.509 Certificate for PIV Attestation", hierarchy.Device.ExportCertificatePem()),
            On("PIV", "X.509 Certificate for PIV Attestation 9d", key.AttestationPem),
        };

        Assert.Same(chosen, Assert.Single(CardCertificates.Choices(all)));
        Assert.NotNull(CardCertificates.AttestationFor(all, chosen));
    }

    [Fact]
    public void Evidence_is_recognised_by_its_structure_when_it_carries_no_manufacturer_extension()
    {
        // Labels that say nothing and no 1.3.6.1.4.1.41482.3 extensions anywhere: only the structure is left. The slot
        // attestation shares the reader certificate's key and was issued by a certificate on the token; that issuer is
        // the device's attestation certificate.
        using var holderKey = RSA.Create(2048);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var until = DateTimeOffset.UtcNow.AddDays(30);

        using var reader = new CertificateRequest("CN=holder", holderKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(from, until);
        using var root = new CertificateRequest("CN=root", rootKey, HashAlgorithmName.SHA256).CreateSelfSigned(from, until);
        using var device = new CertificateRequest("CN=device", deviceKey, HashAlgorithmName.SHA256)
            .Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), from, until, [1]);
        using var slot = new CertificateRequest(new X500DistinguishedName("CN=slot"), reader.PublicKey, HashAlgorithmName.SHA256)
            .Create(device.SubjectName, X509SignatureGenerator.CreateForECDsa(deviceKey), from, until, [2]);

        var chosen = On("T", "a", reader.ExportCertificatePem());
        var all = new List<CardCertificates.Found>
        {
            On("T", "b", device.ExportCertificatePem()),
            chosen,
            On("T", "c", slot.ExportCertificatePem()),
        };

        Assert.Same(chosen, Assert.Single(CardCertificates.Choices(all)));
        Assert.NotNull(CardCertificates.AttestationFor(all, chosen));
    }

    [Fact]
    public void A_reader_certificate_whose_issuing_ca_sits_on_the_token_too_is_still_offered()
    {
        // Issued by another certificate on the token is not enough to be evidence: nothing shares a key here.
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var holderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var until = DateTimeOffset.UtcNow.AddDays(30);
        using var ca = new CertificateRequest("CN=ca", caKey, HashAlgorithmName.SHA256).CreateSelfSigned(from, until);
        using var reader = new CertificateRequest("CN=holder", holderKey, HashAlgorithmName.SHA256)
            .Create(ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), from, until, [3]);

        var all = new List<CardCertificates.Found> { On("T", "ca", ca.ExportCertificatePem()), On("T", "holder", reader.ExportCertificatePem()) };

        Assert.Equal(2, CardCertificates.Choices(all).Count);
    }
}
