using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Alcifra.TestSupport;

/// <summary>
/// A TEST attestation hierarchy (ADR 0963), generated per test and never shipped: a root, optionally the manufacturer-style
/// intermediates, a token's attestation certificate (the PIV F9 slot), and per key the slot's attestation certificate.
/// </summary>
/// <remarks>
/// The two shapes the manufacturer uses: older firmware's slot → F9 → root, and newer firmware's (5.7.4 and later)
/// slot → F9 → PIV intermediate → attestation intermediate → root. Linked into the test projects that need it.
/// </remarks>
public sealed class TestAttestationHierarchy : IDisposable
{
    private static readonly DateTimeOffset From = DateTimeOffset.UtcNow.AddDays(-1);
    private static readonly DateTimeOffset Until = DateTimeOffset.UtcNow.AddYears(20);

    private readonly RSA _rootKey;
    private readonly List<(X509Certificate2 Certificate, RSA Key)> _intermediates = [];
    private readonly ECDsa _deviceKey;

    /// <summary>The root's public certificate.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>The token's attestation certificate (F9).</summary>
    public X509Certificate2 Device { get; }

    public string RootPem => Root.ExportCertificatePem();

    /// <summary>F9 first, then the intermediates up to the root: what a client sends as the intermediate PEM.</summary>
    public string IntermediatePem => string.Concat(new[] { Device }.Concat(_intermediates.AsEnumerable().Reverse().Select(i => i.Certificate))
        .Select(c => $"{c.ExportCertificatePem()}\n"));

    /// <summary>The intermediates alone (not F9): what the catalogue would carry as chain-building help.</summary>
    public IReadOnlyList<X509Certificate2> Intermediates => [.. _intermediates.Select(i => i.Certificate)];

    public string DeviceSerial { get; }

    private TestAttestationHierarchy(bool withIntermediates, string name)
    {
        _rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest($"CN={name} Attestation Root", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        Root = rootRequest.CreateSelfSigned(From, Until);

        var issuerName = Root.SubjectName;
        var issuerGenerator = X509SignatureGenerator.CreateForRSA(_rootKey, RSASignaturePadding.Pkcs1);
        if (withIntermediates)
        {
            foreach (var level in new[] { "Attestation Intermediate", "PIV Attestation" })
            {
                var key = RSA.Create(2048);
                var request = new CertificateRequest($"CN={name} {level}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                var certificate = request.Create(issuerName, issuerGenerator, From, Until, Serial());
                _intermediates.Add((certificate, key));
                (issuerName, issuerGenerator) = (certificate.SubjectName, X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1));
            }
        }

        _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DeviceSerial = RandomNumberGenerator.GetInt32(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var device = new CertificateRequest($"CN={name} PIV Attestation Device", _deviceKey, HashAlgorithmName.SHA256);
        Device = device.Create(issuerName, issuerGenerator, From, Until, Serial());
    }

    /// <summary>The older shape: the token's attestation certificate is signed by the root itself.</summary>
    public static TestAttestationHierarchy Direct(string name = "Test") => new(false, name);

    /// <summary>The newer shape: two intermediates between the token's attestation certificate and the root.</summary>
    public static TestAttestationHierarchy WithIntermediates(string name = "Test") => new(true, name);

    /// <summary>A reader key generated "on the token": its certificate, and the token's attestation of it.</summary>
    /// <param name="subject">The certificate's common name.</param>
    /// <param name="email">
    /// The holder's address, as the certificate's e-mail SAN — what enrolment asks for by default since ADR 0971.
    /// </param>
    public TestKey NewKey(string subject, string? email = null)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));
        if (email is { Length: > 0 })
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email);
            request.CertificateExtensions.Add(san.Build());
        }

        using var leaf = request.CreateSelfSigned(From, Until);
        return new TestKey(leaf.ExportCertificatePem(), AttestPem(leaf.PublicKey), IntermediatePem, key);
    }

    /// <summary>The slot attestation certificate this token would issue for <paramref name="publicKey"/>, PEM.</summary>
    public string AttestPem(PublicKey publicKey, string slot = "9d")
    {
        var request = new CertificateRequest(new X500DistinguishedName($"CN=Test PIV Attestation {slot}"), publicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.41482.3.3", [5, 7, 4], false));
        var serial = new AsnWriter(AsnEncodingRules.DER);
        serial.WriteInteger(BigInteger.Parse(DeviceSerial, System.Globalization.CultureInfo.InvariantCulture));
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.41482.3.7", serial.Encode(), false));
        using var slotCertificate = request.Create(Device.SubjectName, X509SignatureGenerator.CreateForECDsa(_deviceKey), From, Until, Serial());
        return slotCertificate.ExportCertificatePem();
    }

    private static byte[] Serial() => RandomNumberGenerator.GetBytes(8);

    public void Dispose()
    {
        Root.Dispose();
        Device.Dispose();
        _rootKey.Dispose();
        _deviceKey.Dispose();
        foreach (var (certificate, key) in _intermediates)
        {
            certificate.Dispose();
            key.Dispose();
        }
    }
}

/// <summary>A test reader key: its certificate, the slot's attestation, the F9-and-up PEM, and the private key.</summary>
public sealed record TestKey(string CertificatePem, string AttestationPem, string IntermediatePem, RSA Key);
