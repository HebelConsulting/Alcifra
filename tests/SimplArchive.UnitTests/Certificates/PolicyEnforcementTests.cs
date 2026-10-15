using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.Infrastructure.Certificates.Policy;

namespace SimplArchive.UnitTests.Certificates;

/// <summary>
/// A tenant's policy applied to a certificate (ADR 0006), and what each refusal SAYS.
/// </summary>
/// <remarks>
/// The messages are the reason the typed constraints exist at all: a generic mechanism can only report
/// "rule 3 did not match", which is the difference between a user re-enrolling correctly and a user filing
/// a ticket. So these assert on what the refusal names, not merely that there was one.
/// </remarks>
public class PolicyEnforcementTests
{
    private const string Holder = "anna@acme.test";

    [Fact]
    public void An_unset_policy_admits_what_the_product_admits
        ()
    {
        using var certificate = Certificate();

        Assert.Null(Refusal(CertificatePolicy.Unset, certificate));
    }

    [Fact]
    public void An_issuer_rule_names_the_issuer_it_got()
    {
        using var certificate = Certificate(subject: "CN=anna@acme.test");
        var policy = new CertificatePolicy { IssuerDnPattern = "^CN=Acme Corporate CA$" };

        var refusal = Refusal(policy, certificate);

        Assert.Contains("^CN=Acme Corporate CA$", refusal, StringComparison.Ordinal);
        Assert.Contains("CN=anna@acme.test", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_common_name_rule_passes_when_it_matches()
    {
        using var certificate = Certificate(subject: "CN=anna@acme.test");

        Assert.Null(Refusal(new CertificatePolicy { SubjectCnPattern = "@acme\\.test$" }, certificate));
    }

    [Fact]
    public void The_account_match_rule_names_BOTH_addresses()
    {
        // The certificate's own idea of its address, read the way the ENVELOPER reads it — so a certificate
        // that passes here is one the enveloper will address to the same person.
        using var certificate = Certificate(email: "someone.else@acme.test");
        var policy = new CertificatePolicy { SanMustMatchAccount = true };

        var refusal = Refusal(policy, certificate);

        Assert.Contains("someone.else@acme.test", refusal, StringComparison.Ordinal);
        Assert.Contains(Holder, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_carrying_the_accounts_address_satisfies_the_account_rule()
    {
        using var certificate = Certificate(email: Holder);

        Assert.Null(Refusal(new CertificatePolicy { SanMustMatchAccount = true }, certificate));
    }

    [Fact]
    public void A_key_size_rule_names_both_sizes()
    {
        using var certificate = Certificate(keyBits: 2048);
        var policy = new CertificatePolicy { MinimumKeySizeBits = 4096 };

        var refusal = Refusal(policy, certificate);

        Assert.Contains("4096", refusal, StringComparison.Ordinal);
        Assert.Contains("2048", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_extended_key_usage_names_the_one_that_is_missing()
    {
        using var certificate = Certificate();
        var policy = new CertificatePolicy { RequiredExtendedKeyUsages = ["1.3.6.1.5.5.7.3.4"] };

        Assert.Contains("1.3.6.1.5.5.7.3.4", Refusal(policy, certificate), StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_rule_reports_the_value_it_found()
    {
        using var certificate = Certificate(subject: "CN=anna@acme.test, O=Contoso");
        var policy = new CertificatePolicy { Rules = [new CertificateRule("subject:2.5.4.10", "^Acme$")] };

        var refusal = Refusal(policy, certificate);

        Assert.Contains("subject:2.5.4.10", refusal, StringComparison.Ordinal);
        Assert.Contains("Contoso", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_rule_says_ABSENT_rather_than_naming_nothing()
    {
        // A distinct message, because "does not match ''" reads like a bug in the rule rather than a
        // certificate that simply has no such element.
        using var certificate = Certificate(subject: "CN=anna@acme.test");
        var policy = new CertificatePolicy { Rules = [new CertificateRule("subject:2.5.4.10", "^Acme$")] };

        Assert.Contains("is absent", Refusal(policy, certificate), StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_rule_passes_when_the_value_matches()
    {
        using var certificate = Certificate(subject: "CN=anna@acme.test, O=Acme");

        Assert.Null(Refusal(
            new CertificatePolicy { Rules = [new CertificateRule("subject:2.5.4.10", "^Acme$")] }, certificate));
    }

    [Fact]
    public void A_pattern_that_cannot_be_evaluated_fails_CLOSED()
    {
        // Only reachable through a hand-edited policy document, since the surface refuses an invalid
        // pattern. Failing closed keeps the tenant's intent — a rule exists, so be strict — where failing
        // open would admit a certificate BECAUSE the rule aimed at it misbehaved.
        using var certificate = Certificate();
        var policy = new CertificatePolicy { Rules = [new CertificateRule("subject", "*not a regex")] };

        Assert.NotNull(Refusal(policy, certificate));
    }

    [Fact]
    public void A_trust_anchor_rule_refuses_a_certificate_from_another_CA()
    {
        using var otherCa = CaCertificate("CN=Someone Else CA");
        using var certificate = Certificate();
        var policy = new CertificatePolicy { TrustAnchorPem = otherCa.ExportCertificatePem() };

        Assert.Contains("its own certification authority", Refusal(policy, certificate), StringComparison.Ordinal);
    }

    [Fact]
    public void A_CA_certificate_chains_to_ITSELF_as_an_anchor()
    {
        // The degenerate but real case: a tenant whose "CA" is the certificate it issued. It proves the
        // chain builder is given the anchor as CUSTOM root trust rather than consulting the machine store —
        // against the machine store this would fail, since nothing here is publicly trusted.
        //
        // IT MUST BE A CA-CAPABLE CERTIFICATE, and that is a platform fact rather than tidiness: written
        // with the plain reader certificate above, this passed on macOS and FAILED on Linux with
        // "InvalidBasicConstraints: key usage does not include certificate signing" — OpenSSL enforces
        // keyCertSign on a CA where Apple's engine does not, so a self-signed certificate that cannot sign
        // certificates is not a usable anchor on the platform this product actually runs on. Measured on
        // both, 2026-09-30.
        using var certificate = CaCertificate("CN=Acme Corporate CA");
        var policy = new CertificatePolicy { TrustAnchorPem = certificate.ExportCertificatePem() };

        Assert.Null(PolicyEnforcement.Refusal(
            policy, certificate, "CN=Acme Corporate CA", certificate.ExportCertificatePem()));
    }

    // What the LISTING asks, which is a different question from what enrolment asks (ADR 0006): not "may
    // this be enrolled" but "would it still be accepted" — flagged, never enforced.

    [Fact]
    public void An_enrolled_certificate_that_no_longer_passes_is_FLAGGED_rather_than_refused()
    {
        using var certificate = Certificate();
        var policy = new CertificatePolicy { MinimumKeySizeBits = 4096 };

        var failure = PolicyEnforcement.RefusalForEnrolled(policy, certificate.ExportCertificatePem());

        // A reason, not a boolean: an administrator seeing the column has to be able to tell the holder what
        // to ask their CA for.
        Assert.Contains("4096", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enrolled_certificate_that_still_passes_is_not_flagged()
    {
        using var certificate = Certificate();

        Assert.Null(PolicyEnforcement.RefusalForEnrolled(
            new CertificatePolicy { MinimumKeySizeBits = 2048 }, certificate.ExportCertificatePem()));
    }

    [Fact]
    public void The_SAN_rule_does_not_flag_every_row_on_a_tenant_that_set_it()
    {
        // The trap this exists to hold shut. The listing has no account address to compare against — the
        // read model keys on the user id — so evaluating this constraint with a blank one would fail for
        // EVERY certificate, lighting the whole column up on exactly the tenants strict enough to set it.
        // It is switched off explicitly instead, and enforced at enrolment where the address is known.
        using var certificate = Certificate(email: "anna@acme.test");
        var policy = new CertificatePolicy { SanMustMatchAccount = true };

        Assert.Null(PolicyEnforcement.RefusalForEnrolled(policy, certificate.ExportCertificatePem()));
        Assert.Null(PolicyEnforcement.RefusalForEnrolled(
            policy, Certificate(email: "somebody.else@elsewhere.test").ExportCertificatePem()));
    }

    [Fact]
    public void An_unset_policy_flags_a_certificate_that_meets_the_product_floor()
    {
        // THIS TEST USED TO ASSERT SOMETHING STRONGER — that an unset policy flags nothing "without parsing
        // anything", proven by passing a string that is not a certificate. That property died when the
        // key-strength floor became the PRODUCT's rather than the tenant's (owner-decided 2026-09-30): an
        // unset policy now asserts something, so the listing has to parse each row to evaluate it. The
        // saving was real and is gone deliberately; a floor that skipped exactly the installations with no
        // policy would be a floor for nobody.
        //
        // What remains true, and is what this now pins: a tenant that has stated nothing flags nothing about
        // a certificate that meets the floor.
        using var certificate = Certificate();

        Assert.Null(PolicyEnforcement.RefusalForEnrolled(
            CertificatePolicy.Unset, certificate.ExportCertificatePem()));
    }

    [Fact]
    public void An_unset_policy_still_flags_a_WEAK_key()
    {
        // The other half, and the reason the short-circuit had to go: an installation that never opened the
        // policy surface is exactly the one whose weak certificate nothing else would report.
        using var weak = Certificate(keyBits: 1024);

        var refusal = PolicyEnforcement.RefusalForEnrolled(
            CertificatePolicy.Unset, weak.ExportCertificatePem());

        Assert.NotNull(refusal);
        Assert.Contains("2048", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_certificate_is_flagged_even_when_the_policy_is_unset()
    {
        // The case the old short-circuit silently swallowed: a row nothing can parse was reported as fine on
        // any tenant without a policy. It is not fine — nothing can envelope to it either.
        var refusal = PolicyEnforcement.RefusalForEnrolled(CertificatePolicy.Unset, "not a certificate");

        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_stored_certificate_that_no_longer_parses_is_flagged_rather_than_thrown()
    {
        // One unreadable row must not take the pane down. It reads as failing, which is honest: a bundle
        // nothing can parse is one nothing can envelope to either.
        var failure = PolicyEnforcement.RefusalForEnrolled(
            new CertificatePolicy { MinimumKeySizeBits = 2048 }, "-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----");

        Assert.Contains("can no longer be read", failure, StringComparison.Ordinal);
    }

    private static string? Refusal(CertificatePolicy policy, X509Certificate2 certificate) =>
        PolicyEnforcement.Refusal(policy, certificate, Holder, certificate.ExportCertificatePem());

    /// <summary>An ordinary reader certificate: it can receive an envelope and nothing else.</summary>
    private static X509Certificate2 Certificate(
        string subject = "CN=anna@acme.test", string? email = null, int keyBits = 2048) =>
        Build(subject, email, keyBits, certificateAuthority: false);

    /// <summary>
    /// A certificate that can stand in as a trust ANCHOR — CA basic constraints AND keyCertSign.
    /// </summary>
    /// <remarks>
    /// Separate from the reader certificate above because the two really are different, and because getting
    /// it wrong is platform-divergent: Linux refuses a CA whose key usage omits keyCertSign while macOS
    /// accepts it, so a fixture that conflates them is green locally and red in CI. It keeps
    /// KeyEncipherment too, so it also satisfies Encryption Module ADR 0004 when it plays the leaf.
    /// </remarks>
    private static X509Certificate2 CaCertificate(string subject) =>
        Build(subject, email: null, keyBits: 2048, certificateAuthority: true);

    private static X509Certificate2 Build(string subject, string? email, int keyBits, bool certificateAuthority)
    {
        using var key = RSA.Create(keyBits);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            certificateAuthority
                ? X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign
                : X509KeyUsageFlags.KeyEncipherment,
            critical: false));

        if (email is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email);
            request.CertificateExtensions.Add(san.Build());
        }

        if (certificateAuthority)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
