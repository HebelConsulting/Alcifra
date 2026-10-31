using Alcifra.Infrastructure.Certificates.Policy;

namespace Alcifra.UnitTests.Certificates;

/// <summary>
/// What the policy surface refuses before storing (ADR 0006), and why refusing at the door is the point.
/// </summary>
/// <remarks>
/// A rule that cannot work matches NOTHING, and on a sealed-delivery tenant a policy matching nothing locks
/// every reader out of their own documents — with saving an admin form as the act that did it. These are the
/// cases where that would otherwise happen quietly.
/// </remarks>
public class CertificatePolicyValidationTests
{
    [Fact]
    public void An_unset_policy_is_usable_and_asks_for_nothing()
    {
        Assert.True(CertificatePolicy.Unset.IsUnset);
        Assert.Empty(CertificatePolicy.Unset.Problems());
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_refused()
    {
        var policy = new CertificatePolicy { SubjectCnPattern = "^(unclosed" };

        Assert.Contains("SubjectCnPattern", Assert.Single(policy.Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_addressing_something_unresolvable_is_refused_and_says_what_is_available()
    {
        var policy = new CertificatePolicy { Rules = [new CertificateRule("favourite-colour", ".*")] };

        var problem = Assert.Single(policy.Problems());

        Assert.Contains("favourite-colour", problem, StringComparison.Ordinal);
        // The refusal NAMES the alternatives: an administrator who guessed an address wrong is one read away
        // from the right one, rather than one support ticket away.
        Assert.Contains("subject:{oid}", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("subject:2.5.4.10")]
    [InlineData("issuer:2.5.4.3")]
    [InlineData("san:dns")]
    [InlineData("san:email")]
    [InlineData("serial")]
    [InlineData("thumbprint")]
    [InlineData("subject")]
    [InlineData("issuer")]
    public void Every_documented_address_is_accepted(string element)
    {
        var policy = new CertificatePolicy { Rules = [new CertificateRule(element, "^x")] };

        Assert.Empty(policy.Problems());
    }

    [Theory]
    [InlineData("san:uri")]      // deliberately absent — the framework does not parse it (see SanKinds)
    [InlineData("subject:")]     // an OID-shaped address with no OID
    [InlineData("subject:abc")]  // not dotted digits
    [InlineData("subject:2")]    // a single arc is not an OID
    public void An_address_that_looks_right_but_is_not_is_refused(string element)
    {
        var policy = new CertificatePolicy { Rules = [new CertificateRule(element, "^x")] };

        Assert.NotEmpty(policy.Problems());
    }

    [Theory]
    [InlineData(512)]
    [InlineData(40960)]
    public void A_key_size_outside_the_bounds_is_refused_in_BOTH_directions(int bits)
    {
        // The ceiling matters as much as the floor: a typo of 40960 refuses every certificate in existence,
        // which is the lock-out this validation exists for — and it reads like a stricter policy rather than
        // a broken one.
        var policy = new CertificatePolicy { MinimumKeySizeBits = bits };

        Assert.Contains("MinimumKeySizeBits", Assert.Single(policy.Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundle_with_one_unreadable_anchor_is_refused_and_a_readable_bundle_is_not()
    {
        // ADR 1017: every certificate of the bundle has to be readable; one that is not would silently trust less.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Anchor", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var anchor = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var good = anchor.ExportCertificatePem();

        Assert.Empty(new CertificatePolicy { TrustAnchorPem = good + "\n" + good }.Problems());
        var broken = new CertificatePolicy { TrustAnchorPem = good + "\n-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----" };
        Assert.Contains("TrustAnchorPem", Assert.Single(broken.Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_trust_anchor_is_refused()
    {
        var policy = new CertificatePolicy { TrustAnchorPem = "-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----" };

        Assert.Contains("TrustAnchorPem", Assert.Single(policy.Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_the_first()
    {
        // An administrator fixing a policy should not have to save six times to learn six things.
        var policy = new CertificatePolicy
        {
            SubjectCnPattern = "^(bad",
            MinimumKeySizeBits = 1,
            RequiredExtendedKeyUsages = ["not-an-oid"],
            Rules = [new CertificateRule("nonsense", "*also-bad")],
        };

        Assert.Equal(5, policy.Problems().Count);
    }

    [Fact]
    public void A_policy_round_trips_through_its_stored_form()
    {
        var policy = new CertificatePolicy
        {
            IssuerDnPattern = "^CN=Acme CA",
            SanMustMatchAccount = true,
            MinimumKeySizeBits = 3072,
            RequiredExtendedKeyUsages = ["1.3.6.1.5.5.7.3.4"],
            Rules = [new CertificateRule("subject:2.5.4.10", "^Acme$")],
        };

        var parsed = CertificatePolicy.FromJson(policy.ToJson());

        // NOT Assert.Equal(policy, parsed): two properties are lists, and a record compares a list by
        // REFERENCE, so a policy parsed from its own JSON never equals the original. The same trap the ABI's
        // TenantLicense documents — compare the stored form, which is the identity this artefact has.
        Assert.Equal(policy.ToJson(), parsed.ToJson());
        Assert.Equal("^CN=Acme CA", parsed.IssuerDnPattern);
        Assert.Equal(3072, parsed.MinimumKeySizeBits);
        Assert.Equal(["1.3.6.1.5.5.7.3.4"], parsed.RequiredExtendedKeyUsages);
        Assert.Equal(new CertificateRule("subject:2.5.4.10", "^Acme$"), Assert.Single(parsed.Rules));
    }

    [Fact]
    public void The_holders_address_is_required_unless_the_tenant_says_otherwise()
    {
        // ADR 0971: on by default — for a tenant that never saved a policy, and for a stored policy that lacks the
        // field — and off only where it was stated.
        Assert.True(CertificatePolicy.Unset.SanMustMatchAccount);
        Assert.True(new CertificatePolicy().SanMustMatchAccount);
        Assert.True(CertificatePolicy.FromJson("{\"minimumKeySizeBits\": 3072}").SanMustMatchAccount);
        Assert.False(CertificatePolicy.FromJson("{\"sanMustMatchAccount\": false}").SanMustMatchAccount);
    }

    [Fact]
    public void The_binding_reads_policies_saved_before_it_and_either_signal_for_possession_wins()
    {
        // ADR 1017: a stored policy without the binding says it with the address rule alone.
        Assert.Equal(CertificateBinding.Strict, CertificatePolicy.Unset.EffectiveBinding);
        Assert.Equal(CertificateBinding.Strict, CertificatePolicy.FromJson("{\"minimumKeySizeBits\": 3072}").EffectiveBinding);
        Assert.Equal(CertificateBinding.Possession, CertificatePolicy.FromJson("{\"sanMustMatchAccount\": false}").EffectiveBinding);

        // Stored by name, read back by name.
        var stored = new CertificatePolicy { Binding = CertificateBinding.Possession, SanMustMatchAccount = false }.ToJson();
        Assert.Contains("\"binding\":\"Possession\"", stored, StringComparison.Ordinal);
        Assert.Equal(CertificateBinding.Possession, CertificatePolicy.FromJson(stored).EffectiveBinding);

        // An older client switching the address rule off while echoing a Strict binding still leaves Strict.
        Assert.Equal(CertificateBinding.Possession, new CertificatePolicy { Binding = CertificateBinding.Strict, SanMustMatchAccount = false }.EffectiveBinding);
        Assert.False(new CertificatePolicy { Binding = CertificateBinding.Possession }.IsUnset);
    }

    [Fact]
    public void An_opt_out_is_kept_in_the_stored_form()
    {
        var parsed = CertificatePolicy.FromJson(new CertificatePolicy { SanMustMatchAccount = false }.ToJson());

        Assert.False(parsed.SanMustMatchAccount);
        Assert.False(parsed.IsUnset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"rules\": \"wrong shape\"}")]
    public void An_unreadable_stored_policy_reads_as_UNSET_rather_than_throwing(string? stored)
    {
        // The safe direction on the ENROLMENT path: a policy document somebody edited by hand into nonsense
        // must make enrolment unconstrained, not impossible. Refusing everything would be indistinguishable
        // at the door from a strict policy, leaving an administrator debugging a rule they cannot see.
        Assert.True(CertificatePolicy.FromJson(stored).IsUnset);
    }
}
