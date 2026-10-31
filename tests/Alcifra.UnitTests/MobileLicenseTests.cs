using System.Security.Cryptography;
using SimplArchive.ModuleAbi;

namespace Alcifra.UnitTests;

// Core ADR 0954: the Alcifra Mobile app's licence, signed as a tenant licence is and verified offline by the app.
public sealed class MobileLicenseTests
{
    private static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static MobileLicense Licence(string level = MobileLicense.Pro, params string[] domains) =>
        new MobileLicense(level, domains.Length == 0 ? ["kristall.ch", "Kristall-Holding.CH"] : domains, "Kristall Holding AG",
            new DateOnly(2027, 10, 20), string.Empty).Sign(Vendor);

    [Fact]
    public void A_signed_licence_verifies_and_any_changed_claim_does_not()
    {
        var licence = Licence();

        Assert.True(licence.VerifiedBy(Vendor));
        Assert.False((licence with { Level = MobileLicense.Standard }).VerifiedBy(Vendor));
        Assert.False((licence with { Domains = ["example.com"] }).VerifiedBy(Vendor));
        Assert.False((licence with { ValidUntil = new DateOnly(2030, 1, 1) }).VerifiedBy(Vendor));
        Assert.False((licence with { Signature = "not base64!" }).VerifiedBy(Vendor));
        Assert.False(licence.VerifiedBy(ECDsa.Create(ECCurve.NamedCurves.nistP256)));
    }

    [Fact]
    public void The_domains_order_and_case_do_not_change_what_is_signed() =>
        Assert.True((Licence() with { Domains = ["KRISTALL-holding.ch", "Kristall.CH"] }).VerifiedBy(Vendor));

    [Fact]
    public void A_tenant_licence_signature_never_passes_as_a_mobile_licence()
    {
        var tenant = new TenantLicense(["messenger"], Guid.NewGuid(), new DateOnly(2027, 10, 20), 1, string.Empty).Sign(Vendor);

        Assert.False((Licence() with { Signature = tenant.Signature }).VerifiedBy(Vendor));
        Assert.StartsWith($"{MobileLicense.Kind}\n", Licence().SignedPayload(), StringComparison.Ordinal);
    }

    // Rename phase 4b (#1117): a licence issued before it says "simplarchive-mobile" in its signed payload and still
    // unlocks the app until v0.57.0; a payload of any OTHER kind still does not.
    [Fact]
    public void A_licence_signed_under_the_kind_before_the_rename_still_verifies()
    {
        var licence = Licence();
        var before = $"{MobileLicense.LegacyKind}{licence.SignedPayload()[MobileLicense.Kind.Length..]}";
        var other = $"tenant{licence.SignedPayload()[MobileLicense.Kind.Length..]}";

        Assert.True((licence with { Signature = Sign(before) }).VerifiedBy(Vendor));
        Assert.False((licence with { Signature = Sign(other) }).VerifiedBy(Vendor));
        Assert.Equal("alcifra-mobile", MobileLicense.Kind);
    }

    // As the vendor signs a payload: ECDsa P-256/SHA-256 over its UTF-8 bytes, base64.
    private static string Sign(string payload) =>
        Convert.ToBase64String(Vendor.SignData(System.Text.Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

    [Theory]
    [InlineData("petra@kristall.ch", true)]
    [InlineData("Petra@KRISTALL.CH", true)]
    [InlineData("anna@kristall-holding.ch", true)]
    [InlineData("tom@mail.kristall.ch", false)]   // a subdomain is a different domain
    [InlineData("eve@kristall.ch.evil.example", false)]
    [InlineData("kristall.ch", false)]
    [InlineData(null, false)]
    public void It_unlocks_the_profiles_in_its_domains_only(string? userName, bool unlocks) =>
        Assert.Equal(unlocks, Licence().Unlocks(userName));

    [Fact]
    public void It_is_valid_through_its_last_day_and_says_whether_it_is_pro()
    {
        Assert.True(Licence().IsValidOn(new DateOnly(2027, 10, 20)));
        Assert.False(Licence().IsValidOn(new DateOnly(2027, 10, 21)));
        Assert.True(Licence().IsPro);
        Assert.False(Licence(MobileLicense.Standard).IsPro);
    }
}
