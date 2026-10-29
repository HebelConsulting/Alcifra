using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Alcifra.Domain.Certificates;
using Alcifra.Infrastructure.Certificates.Attestation;
using Alcifra.TestSupport;

namespace Alcifra.UnitTests.Certificates;

// The PIV-form attestation verifier (#1790, ADR 0963), against generated hierarchies of BOTH shapes the manufacturer uses:
// older firmware's slot → F9 → root, and newer firmware's slot → F9 → two intermediates → root. Each refusal by name.
public class PivAttestationVerifierTests
{
    private static AttestationAnchor Anchor(TestAttestationHierarchy h) =>
        new(AttestationCatalogue.Thumbprint(h.Root), h.Root);

    private static AttestationVerdict Verify(TestKey key, TestAttestationHierarchy anchorOf, IReadOnlyList<X509Certificate2>? helpers = null,
        string? attestationPem = null, string? intermediatePem = null)
    {
        using var leaf = X509Certificate2.CreateFromPem(key.CertificatePem);
        return PivAttestationVerifier.Verify(
            new AttestationEvidence("piv", attestationPem ?? key.AttestationPem, intermediatePem ?? key.IntermediatePem, "9d"),
            leaf, [Anchor(anchorOf)], helpers ?? []);
    }

    [Fact]
    public void The_older_shape_slot_to_F9_to_root_verifies_and_names_root_serial_and_firmware()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var verdict = Verify(hierarchy.NewKey("alice"), hierarchy);
        Assert.Equal(AttestationOutcome.Verified, verdict.Outcome);
        Assert.Equal(AttestationCatalogue.Thumbprint(hierarchy.Root), verdict.RootThumbprint);
        Assert.Equal(hierarchy.DeviceSerial, verdict.DeviceSerial);
        Assert.Equal("5.7.4", verdict.FirmwareVersion);
    }

    [Fact]
    public void The_newer_shape_with_intermediates_verifies_whether_the_client_or_the_catalogue_supplies_them()
    {
        using var hierarchy = TestAttestationHierarchy.WithIntermediates();
        var key = hierarchy.NewKey("bob");
        Assert.True(Verify(key, hierarchy).Verified);

        // A client that sends only F9 (as a token holds no intermediates): the published intermediates complete the chain.
        var onlyDevice = hierarchy.Device.ExportCertificatePem();
        Assert.Equal(AttestationOutcome.RootUnknown, Verify(key, hierarchy, intermediatePem: onlyDevice).Outcome);
        Assert.True(Verify(key, hierarchy, helpers: hierarchy.Intermediates, intermediatePem: onlyDevice).Verified);
    }

    [Fact]
    public void A_chain_to_another_root_is_unknown()
    {
        using var hierarchy = TestAttestationHierarchy.Direct("Ours");
        using var other = TestAttestationHierarchy.Direct("Theirs");
        Assert.Equal(AttestationOutcome.RootUnknown, Verify(other.NewKey("mallory"), hierarchy).Outcome);
    }

    [Fact]
    public void A_genuine_attestation_of_a_different_key_is_a_key_mismatch()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var enrolled = hierarchy.NewKey("alice");
        var another = hierarchy.NewKey("other");
        Assert.Equal(AttestationOutcome.KeyMismatch, Verify(enrolled, hierarchy, attestationPem: another.AttestationPem).Outcome);
    }

    [Fact]
    public void A_tampered_signature_is_invalid_not_unknown()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var key = hierarchy.NewKey("alice");
        using var slot = X509Certificate2.CreateFromPem(key.AttestationPem);
        var bytes = slot.RawData.ToArray();
        bytes[^5] ^= 0x5A;   // inside the signature value
        var tampered = PemEncoding.WriteString("CERTIFICATE", bytes);
        Assert.Equal(AttestationOutcome.SignatureInvalid, Verify(key, hierarchy, attestationPem: tampered).Outcome);

        // A token certificate claiming the root as issuer but signed by a different key: tampering, named so.
        using var impostor = TestAttestationHierarchy.Direct();   // same subject names ("Test …"), different keys
        Assert.Equal(AttestationOutcome.SignatureInvalid, Verify(impostor.NewKey("eve"), hierarchy).Outcome);
    }

    [Fact]
    public void Unreadable_evidence_and_an_unknown_format_are_malformed()
    {
        using var hierarchy = TestAttestationHierarchy.Direct();
        var key = hierarchy.NewKey("alice");
        Assert.Equal(AttestationOutcome.Malformed, Verify(key, hierarchy, attestationPem: "not a certificate").Outcome);
        Assert.Equal(AttestationOutcome.Malformed, Verify(key, hierarchy, intermediatePem: string.Empty).Outcome);
        using var leaf = X509Certificate2.CreateFromPem(key.CertificatePem);
        Assert.Equal(AttestationOutcome.Malformed, PivAttestationVerifier.Verify(
            new AttestationEvidence("tpm", key.AttestationPem, key.IntermediatePem, null), leaf, [Anchor(hierarchy)], []).Outcome);
    }

    [Theory]
    [InlineData(true, false, false, AttestationState.Attested, true)]
    [InlineData(false, false, false, AttestationState.RootNotTrusted, false)]   // switched off for the installation
    [InlineData(true, true, false, AttestationState.RootNotTrusted, false)]     // switched off by the tenant
    [InlineData(true, true, true, AttestationState.RootNotTrusted, true)]       // ... where the tenant admits unattested hardware
    public void A_root_switch_decides_without_touching_the_certificate(bool enabled, bool tenantDisabled, bool unattestedAllowed,
        AttestationState state, bool addresses)
    {
        const string root = "AB";
        var trust = new AttestationTrust(unattestedAllowed,
            enabled && !tenantDisabled ? new HashSet<string> { root } : [], new Dictionary<string, string> { [root] = "Root" });
        Assert.Equal(state, trust.StateOf(root));
        Assert.Equal(addresses, trust.Addresses(root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void An_unattested_certificate_addresses_its_holder_only_where_the_tenant_allows_it(bool allowed, bool addresses)
    {
        var trust = new AttestationTrust(allowed, new HashSet<string>(), new Dictionary<string, string>());
        Assert.Equal(AttestationState.NotAttested, trust.StateOf(null));
        Assert.Equal(addresses, trust.Addresses(null));
    }

    [Fact]
    public void The_catalogue_ships_both_manufacturer_roots_with_their_published_fingerprints_and_the_PIV_intermediates()
    {
        var thumbprints = AttestationCatalogue.Roots
            .Select(e => { using var c = X509Certificate2.CreateFromPem(AttestationCatalogue.Pem(e.Resource)); return AttestationCatalogue.Thumbprint(c); })
            .ToList();
        Assert.Equal(
            ["63ECE914E54DD87915F34033C85AF4C0696BA1512F8ADD66CED738331207B546", "62760C6A6EF91679F454C8902B80FD009825B3F25DA90F1FBACE2EC6586CD5A8"],
            thumbprints);
        Assert.Equal(5, AttestationCatalogue.Intermediates.Count);
        Assert.DoesNotContain(AttestationCatalogue.Roots, e => e.Key == AttestationCatalogue.TestRootKey);
    }

    [Fact]
    public void The_published_intermediates_chain_to_the_newer_catalogue_root()
    {
        using var root = X509Certificate2.CreateFromPem(AttestationCatalogue.Pem("attestation-root-2024"));
        var intermediates = AttestationCatalogue.Intermediates;
        var top = intermediates.Where(i => CertificateSignature.IsSignedBy(i, root)).ToList();
        Assert.Equal(2, top.Count);
        Assert.All(intermediates.Except(top), i => Assert.Contains(top, t => CertificateSignature.IsSignedBy(i, t)));
    }

    [Fact]
    public void Outside_Development_a_trusted_test_root_refuses_the_start_and_a_switched_off_one_does_not()
    {
        TrustedAttestationRoot Test(bool enabled) => new() { CatalogueKey = AttestationCatalogue.TestRootKey, Enabled = enabled };
        var production = new Environment("Production");
        Assert.Throws<InvalidOperationException>(() => AttestationRootCatalogueSeeder.RefuseTestRootOutsideDevelopment([Test(true)], production));
        AttestationRootCatalogueSeeder.RefuseTestRootOutsideDevelopment([Test(false)], production);
        AttestationRootCatalogueSeeder.RefuseTestRootOutsideDevelopment([Test(true)], new Environment("Development"));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
