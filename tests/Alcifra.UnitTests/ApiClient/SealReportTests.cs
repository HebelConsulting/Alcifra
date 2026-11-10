using Alcifra.ApiClient.Inspection;
using static Alcifra.UnitTests.SealedArchive.SealedArchiveTestKeys;

namespace Alcifra.UnitTests.ApiClient;

// The inspection view's report (#2291): a local file's inspection, its recipients named where the installation knows the
// certificate and listed by reference where it does not, with the ASN.1 structure for the third level.
public class SealReportTests
{
    [Fact]
    public void A_local_envelope_names_the_known_recipient_and_keeps_the_unknown_one_by_reference()
    {
        var anna = Rsa("Anna Muster");
        var stranger = Rsa("Somebody Else");
        var inspection = SealedInspector.Inspect(Alcifra.ApiClient.SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [PublicOnly(anna), PublicOnly(stranger)]));
        var known = new KnownCertificate(anna.Issuer, anna.SerialNumber, null, "YubiKey 5C", "Anna Muster", "anna@example.test", null, "attested");

        var report = SealReport.Of(inspection, [known]);

        Assert.Equal(SealedKind.Enveloped, report.Kind);
        var named = Assert.Single(report.Recipients, r => r.IsKnown);
        Assert.Equal(("Anna Muster", "YubiKey 5C"), (named.HolderName, named.Label));
        var unknown = Assert.Single(report.Recipients, r => !r.IsKnown);
        Assert.True(unknown.Reference.Is(stranger));
        Assert.Null(report.Audience);
        Assert.NotEmpty(report.Asn1);
    }
}
