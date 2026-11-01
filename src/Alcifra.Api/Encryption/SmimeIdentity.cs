using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Alcifra.Api.Encryption;

/// <summary>
/// The S/MIME certificate profile a SELF-SIGNED certificate needs to be encrypted to — keyEncipherment (RSA S/MIME wraps
/// the content key with the recipient's key), the emailProtection EKU, and the address as an rfc822Name SAN so a mail
/// client matches the certificate to the address. The tenant's mail-in ingest key (#1335, ADR 0820) is its one user since
/// the self-service identity it was written for retired (#1970 slice 4).
/// </summary>
public static class SmimeIdentity
{
    private const string EmailProtectionOid = "1.3.6.1.5.5.7.3.4";

    /// <summary>The S/MIME certificate profile for <paramref name="email"/>, self-signed over <paramref name="key"/>.</summary>
    public static X509Certificate2 SelfSign(string email, RSA key, TimeSpan lifetime)
    {
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(EmailProtectionOid)], critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);   // clock-skew grace, the usual issuance courtesy
        return request.CreateSelfSigned(notBefore, notBefore + lifetime);
    }
}
