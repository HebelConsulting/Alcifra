using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Api.Errors.Exceptions.OfflineArchives;
using Alcifra.Api.OfflineArchives;

namespace Alcifra.UnitTests.SealedArchive;

// What may be the tenant's recovery certificate (ADR 0960): the public certificate alone, one of it, a key an envelope
// can be addressed to, not expired on the day it is set. A private key in the upload is refused, never stripped.
public class RecoveryCertificateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");

    private static (string Certificate, string Key) Rsa(DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Acme recovery", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddYears(-1), notAfter);
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    [Fact]
    public void The_public_certificate_alone_is_accepted_and_described()
    {
        var (certificate, _) = Rsa(Now.AddYears(5));

        var accepted = RecoveryCertificate.Validate($"some notes\n{certificate}\n", Now);

        Assert.Equal(certificate, accepted.CertificatePem);
        Assert.Equal("CN=Acme recovery", accepted.Subject);
        Assert.Equal(64, accepted.Thumbprint.Length);
    }

    [Fact]
    public void An_upload_with_its_private_key_is_refused_not_stripped()
    {
        var (certificate, key) = Rsa(Now.AddYears(5));

        var refusal = Assert.Throws<InvalidRecoveryCertificateException>(() => RecoveryCertificate.Validate(certificate + key, Now));
        Assert.Contains("private key", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_certificates_none_or_garbage_are_refused()
    {
        var (one, _) = Rsa(Now.AddYears(5));
        var (two, _) = Rsa(Now.AddYears(5));

        Assert.Throws<InvalidRecoveryCertificateException>(() => RecoveryCertificate.Validate(one + two, Now));
        Assert.Throws<InvalidRecoveryCertificateException>(() => RecoveryCertificate.Validate("no certificate here", Now));
        Assert.Throws<InvalidRecoveryCertificateException>(() => RecoveryCertificate.Validate("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----", Now));
    }

    [Fact]
    public void A_certificate_already_expired_is_refused()
    {
        var (certificate, _) = Rsa(Now.AddDays(-1));

        Assert.Throws<InvalidRecoveryCertificateException>(() => RecoveryCertificate.Validate(certificate, Now));
    }
}
