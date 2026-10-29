using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Infrastructure.Certificates.Policy;

namespace Alcifra.UnitTests.Certificates;

// The key-strength floor, which is PER ALGORITHM and applies whether or not a tenant has set one (ADR 0006,
// owner-decided 2026-09-30).
//
// WHY A FLOOR AT ALL. Every recipient of an envelope is an independent way in — ADR 0842 addresses a reader's
// whole certificate set — so a single weak key is a weak door on every document that reader can open, while
// the AES-256 content encryption and every other recipient look perfectly healthy. Nothing reports it. The
// field existed and was applied only when a tenant SET it, so an installation that never wrote a policy had
// no minimum at all.
//
// WHY PER ALGORITHM, which is the half that had to change rather than merely gain a default: a P-256 key
// measures 256 bits and an RSA-2048 key measures 2048, and the single minimum was compared against whichever
// KeySize returned. A floor of 2048 therefore refuses every EC certificate — a PIV card's included, which is
// the ordinary case this product serves — and a floor of 256 permits RSA-256. RSA gets a number; EC gets a
// list of curves.
public class KeyStrengthPolicyTests
{
    [Fact]
    public void A_weak_RSA_key_is_refused_even_though_the_tenant_set_nothing()
    {
        using var certificate = Rsa(1024);

        var refusal = PolicyEnforcement.Refusal(
            CertificatePolicy.Unset, certificate, "anna@acme.test", certificate.ExportCertificatePem());

        Assert.NotNull(refusal);
        Assert.Contains("2048", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_RSA_key_at_the_default_floor_passes()
    {
        using var certificate = Rsa(2048);

        Assert.Null(PolicyEnforcement.Refusal(
            CertificatePolicy.Unset, certificate, "anna@acme.test", certificate.ExportCertificatePem()));
    }

    [Fact]
    public void A_P256_CARD_certificate_passes_the_default_floor()
    {
        // THE REGRESSION THIS EXISTS FOR. A single 2048-bit minimum would refuse this, and it is exactly the
        // certificate a YubiKey issues in slot 9D — so the floor would have made every card unusable while
        // reading as a security improvement.
        using var certificate = Ec(ECCurve.NamedCurves.nistP256);

        Assert.Null(PolicyEnforcement.Refusal(
            CertificatePolicy.Unset, certificate, "anna@acme.test", certificate.ExportCertificatePem()));
    }

    [Fact]
    public void An_explicit_2048_minimum_still_does_not_refuse_an_EC_certificate()
    {
        // The same trap from the other side: a tenant that raises the RSA floor must not thereby lock out
        // every card holder. The number is RSA's and says so.
        using var certificate = Ec(ECCurve.NamedCurves.nistP256);

        Assert.Null(PolicyEnforcement.Refusal(
            new CertificatePolicy { MinimumKeySizeBits = 2048 },
            certificate,
            "anna@acme.test",
            certificate.ExportCertificatePem()));
    }

    // A curve outside the default set is proven by A_tenant_naming_only_P384_refuses_a_P256_certificate
    // below, from the other direction. Doing it with a genuinely non-NIST curve was tried and is not
    // portable: macOS answers PlatformNotSupportedException for secp256k1 ("the specified curve 1.3.132.0.10
    // or its parameters are not valid for this platform"), so the fixture would be green on Linux and red
    // here — the platform-divergence trap this file's CA helper already carries a note about.

    [Theory]
    [InlineData("secp256r1")]      // BouncyCastle's registry name — what a tenant is asked to write
    [InlineData("prime256v1")]     // its documented alias
    [InlineData("P-256")]          // what .NET and most tools print
    [InlineData("1.2.840.10045.3.1.7")]
    public void A_tenant_may_name_the_curve_in_any_accepted_spelling(string spelling)
    {
        // Refusing a correct curve over its spelling is a refusal nobody can act on — and a certificate
        // reports its curve as an OID on one platform and a friendly name on another.
        using var certificate = Ec(ECCurve.NamedCurves.nistP256);

        Assert.Null(PolicyEnforcement.Refusal(
            new CertificatePolicy { PermittedCurves = [spelling] },
            certificate,
            "anna@acme.test",
            certificate.ExportCertificatePem()));
    }

    [Fact]
    public void A_tenant_naming_only_P384_refuses_a_P256_certificate()
    {
        // The anti-vacuous half of the spelling tolerance: a matcher that said yes to everything would pass
        // every case above.
        using var certificate = Ec(ECCurve.NamedCurves.nistP256);

        Assert.NotNull(PolicyEnforcement.Refusal(
            new CertificatePolicy { PermittedCurves = ["secp384r1"] },
            certificate,
            "anna@acme.test",
            certificate.ExportCertificatePem()));
    }

    [Fact]
    public void A_tenant_may_state_a_LOWER_RSA_floor_than_the_default()
    {
        // Absence means the default; a deliberate lower number is a tenant's decision and is honoured. That
        // is what keeps the default a default rather than a hard product rule.
        using var certificate = Rsa(1024);

        Assert.Null(PolicyEnforcement.Refusal(
            new CertificatePolicy { MinimumKeySizeBits = 1024 },
            certificate,
            "anna@acme.test",
            certificate.ExportCertificatePem()));
    }

    private static X509Certificate2 Rsa(int bits)
    {
        using var key = RSA.Create(bits);
        var request = new CertificateRequest(
            "CN=anna@acme.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(HoldersAddress());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>An EC certificate, self-signed — which is permitted because one EC key can do ECDSA too.</summary>
    /// <remarks>
    /// Key usage is left at <c>keyAgreement</c> deliberately: it is what a card issues, and the strength rule
    /// must read the key through a getter that answers for such a certificate. Self-signing works here only
    /// because the usage is not KeyAgreement-ONLY in .NET's eyes — an agreement-only request cannot be
    /// self-signed, which is why the hardware fixtures elsewhere are CA-issued.
    /// </remarks>
    private static X509Certificate2 Ec(ECCurve curve)
    {
        using var key = ECDsa.Create(curve);
        var request = new CertificateRequest("CN=anna@acme.test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(HoldersAddress());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    // The holder's own address, which an unset policy asks for since ADR 0971 — so these isolate the strength rule.
    private static X509Extension HoldersAddress()
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress("anna@acme.test");
        return san.Build();
    }
}
