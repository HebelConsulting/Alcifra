using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Infrastructure.Certificates;

namespace Alcifra.UnitTests.Certificates;

// Which certificate in a bundle addresses the reader (ADR 0002).
//
// THE HAZARD THIS GUARDS is not "the wrong certificate is chosen" in the abstract. It is that choosing the
// ISSUER means enveloping to the CA: the certificate authority could open the document and the reader could
// not. A chain exported leaf-last — which several tools do — is all it takes, so every test here presents the
// bundle in an order that would defeat a positional implementation.
public class CertificateChainTests
{
    [Fact]
    public void A_single_certificate_is_its_own_leaf()
    {
        var (pem, _) = SelfSigned("CN=alone");

        var result = CertificateChain.Read(pem);

        Assert.True(result.Ok);
        Assert.Equal("CN=alone", result.Leaf!.Subject);
    }

    [Fact]
    public void The_leaf_is_found_even_when_the_bundle_is_ROOT_FIRST()
    {
        // The order that breaks a positional reader, and the one openssl produces from some pipelines.
        var (rootPem, rootKey, root) = Ca("CN=Acme Root");
        var (leafPem, _) = IssuedBy(root, rootKey, "CN=reader");

        var result = CertificateChain.Read(rootPem + leafPem);

        Assert.True(result.Ok);
        Assert.Equal("CN=reader", result.Leaf!.Subject);
        Assert.Equal(2, result.All.Count);
    }

    [Fact]
    public void A_three_deep_chain_resolves_to_the_end_entity()
    {
        var (rootPem, rootKey, root) = Ca("CN=Acme Root");
        var (intermediatePem, intermediateKey, intermediate) = CaIssuedBy(root, rootKey, "CN=Acme Issuing");
        var (leafPem, _) = IssuedBy(intermediate, intermediateKey, "CN=reader card");

        var result = CertificateChain.Read(rootPem + intermediatePem + leafPem);

        Assert.True(result.Ok);
        Assert.Equal("CN=reader card", result.Leaf!.Subject);
    }

    [Fact]
    public void TWO_end_entities_are_refused_rather_than_guessed()
    {
        // The case where guessing is indistinguishable from working: both are plausible leaves, so a
        // positional reader would pick one and be right half the time — and wrong silently the other half.
        var (rootPem, rootKey, root) = Ca("CN=Acme Root");
        var (onePem, _) = IssuedBy(root, rootKey, "CN=reader one");
        var (twoPem, _) = IssuedBy(root, rootKey, "CN=reader two");

        var result = CertificateChain.Read(rootPem + onePem + twoPem);

        Assert.False(result.Ok);
        Assert.Contains("ambiguous", result.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nonsense_is_refused_with_a_reason_a_person_can_act_on()
    {
        var result = CertificateChain.Read("not a certificate");

        Assert.False(result.Ok);
        Assert.NotNull(result.Refusal);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    // ONE instant for every certificate in a test, and every validity derived from it.
    //
    // This is not tidiness. Each helper used to call DateTimeOffset.UtcNow again, and RSA-2048 keygen takes
    // long enough that a chain's calls straddle a second boundary — so a leaf's notAfter came out LATER than
    // the intermediate that signed it, and .NET refuses outright:
    //   "The requested notAfter value (…:57) is later than issuerCertificate.NotAfter (…:56)"
    // Observed once in a run here. It fires on roughly whichever test happens to cross the boundary, which
    // is what makes it read as an unrelated breakage rather than as a clock.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static (string Pem, RSA Signer) SelfSigned(string subject)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return (certificate.ExportCertificatePem() + "\n", key);
    }

    private static (string Pem, RSA Signer, X509Certificate2 Certificate) Ca(string subject)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var certificate = request.CreateSelfSigned(Now.AddDays(-2), Now.AddYears(5));
        return (certificate.ExportCertificatePem() + "\n", key, certificate);
    }

    private static (string Pem, RSA Signer, X509Certificate2 Certificate) CaIssuedBy(
        X509Certificate2 issuer, RSA issuerKey, string subject)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        // Signed certificates come back WITHOUT their private key, unlike self-signed ones -- so an
        // intermediate has to be handed its key before it can sign the leaf in turn.
        var certificate = Sign(request, issuer, issuerKey);
        return (certificate.ExportCertificatePem() + "\n", key, certificate.CopyWithPrivateKey(key));
    }

    private static (string Pem, RSA Signer) IssuedBy(X509Certificate2 issuer, RSA issuerKey, string subject)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: false));
        return (Sign(request, issuer, issuerKey).ExportCertificatePem() + "\n", key);
    }

    // CreateSelfSigned already attaches the private key, so the issuer signs directly -- CopyWithPrivateKey
    // throws "the certificate already has an associated private key".
    private static X509Certificate2 Sign(CertificateRequest request, X509Certificate2 issuer, RSA issuerKey)
    {
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        // Never outlive the issuer: an intermediate is itself issued here, so a fixed one-year window would
        // make a three-deep chain's leaf outlast its parent by construction rather than by a race.
        var notAfter = Now.AddYears(1) < issuer.NotAfter ? Now.AddYears(1) : issuer.NotAfter;
        return request.Create(issuer, Now.AddDays(-1), notAfter, serial);
    }
}
