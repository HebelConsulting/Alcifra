using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.Infrastructure.Certificates;

namespace SimplArchive.UnitTests.Certificates;

// What enrolment refuses BEFORE it files (ADR 0002).
//
// Each of these fails in a different direction, which is why they are checked rather than assumed. A private
// key filed into the archive is a disclosure. A signing-only certificate is a document addressed to a key that
// can never read it. And an over-strict usage check refuses the card certificates this product exists to serve.
public class EnrolmentPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void An_ordinary_encipherment_certificate_is_accepted()
    {
        var result = EnrolmentPolicy.Check(Rsa("CN=Alice", X509KeyUsageFlags.KeyEncipherment));

        Assert.True(result.Ok);
        Assert.Contains("Alice", result.Leaf!.Subject);
    }

    [Fact]
    public void An_EC_certificate_declaring_KEY_AGREEMENT_is_accepted()
    {
        // Encryption Module ADR 0002 says "key encipherment", and taking that literally would refuse EVERY EC certificate: an
        // RSA recipient has the content key WRAPPED to it (keyEncipherment) while an EC recipient reaches it
        // through ECDH AGREEMENT (keyAgreement). A card-issued EC certificate carries the second — so the
        // literal reading would have refused exactly the hardware this product is for.
        var result = EnrolmentPolicy.Check(Ec("CN=Card", X509KeyUsageFlags.KeyAgreement));

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_certificate_declaring_NO_key_usage_at_all_is_accepted()
    {
        // X.509 makes key usage UNRESTRICTED when the extension is absent, so refusing one that declares
        // nothing would reject a usable certificate for having made no promises.
        var result = EnrolmentPolicy.Check(Rsa("CN=Unrestricted", usage: null));

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_SIGNING_ONLY_certificate_is_refused_and_told_why()
    {
        var result = EnrolmentPolicy.Check(Rsa("CN=Signer", X509KeyUsageFlags.DigitalSignature));

        Assert.False(result.Ok);
        Assert.Contains("Key Encipherment", result.Refusal);
        Assert.Contains("Key Agreement", result.Refusal);
    }

    [Fact]
    public void A_bundle_carrying_a_PRIVATE_KEY_is_refused()
    {
        // The check that would silently pass if it asked the parsed certificate: ImportFromPem reads
        // CERTIFICATE blocks and IGNORES every other label, so HasPrivateKey is false even for a file that
        // plainly contains one. A check that cannot fail is worse than none, because it looks like one.
        using var key = RSA.Create(2048);
        var pem = Rsa("CN=Leaky", X509KeyUsageFlags.KeyEncipherment) + key.ExportPkcs8PrivateKeyPem() + "\n";

        var result = EnrolmentPolicy.Check(pem);

        Assert.False(result.Ok);
        Assert.Contains("PRIVATE KEY", result.Refusal);
    }

    [Theory]
    [InlineData("PRIVATE KEY")]
    [InlineData("RSA PRIVATE KEY")]
    [InlineData("EC PRIVATE KEY")]
    [InlineData("ENCRYPTED PRIVATE KEY")]
    public void Every_spelling_of_a_private_key_label_is_caught(string label)
    {
        // One substring covers all four because they share their last two words — which is also what makes it
        // cover a spelling some future algorithm invents, where a per-algorithm list would not.
        var pem = Rsa("CN=Leaky", X509KeyUsageFlags.KeyEncipherment)
            + $"-----BEGIN {label}-----\nAAAA\n-----END {label}-----\n";

        Assert.False(EnrolmentPolicy.Check(pem).Ok);
    }

    [Fact]
    public void A_file_that_is_not_a_certificate_is_refused_by_the_reader_beneath()
    {
        var result = EnrolmentPolicy.Check("hello");

        Assert.False(result.Ok);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void An_AMBIGUOUS_bundle_is_refused_rather_than_guessed()
    {
        // Two certificates that issue none of the others: which one addresses the reader is unknowable, and
        // picking either would be a coin toss over who can open the document.
        var result = EnrolmentPolicy.Check(
            Rsa("CN=One", X509KeyUsageFlags.KeyEncipherment) + Rsa("CN=Two", X509KeyUsageFlags.KeyEncipherment));

        Assert.False(result.Ok);
        Assert.NotNull(result.Refusal);
    }

    // --- harness ----------------------------------------------------------------------------------------

    private static string Rsa(string subject, X509KeyUsageFlags? usage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (usage is { } flags)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: false));
        }

        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return certificate.ExportCertificatePem() + "\n";
    }

    // Self-signed through the SIGNATURE GENERATOR rather than CreateSelfSigned, which refuses this shape with
    // "the provided key does not match the public key algorithm for this certificate". A harness detail, but
    // worth keeping the certificate genuinely EC rather than substituting RSA: the point of the test is the
    // certificate a card actually issues.
    private static string Ec(string subject, X509KeyUsageFlags usage)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var certificate = request.Create(
            request.SubjectName, X509SignatureGenerator.CreateForECDsa(key), Now.AddDays(-1), Now.AddYears(1), serial);
        return certificate.ExportCertificatePem() + "\n";
    }
}
