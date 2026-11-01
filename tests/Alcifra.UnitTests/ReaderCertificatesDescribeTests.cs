using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Api.Controllers;
using Alcifra.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace Alcifra.UnitTests;

// The caller's certificate set (#1741) says where it came from and, when empty, why — the four causes ADR 0842 keeps
// apart, with different fixes. Delivery's rule decides the source: a module that speaks is the only one.
public sealed class ReaderCertificatesDescribeTests
{
    private static string Pem(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    [Fact]
    public void A_module_answer_is_the_source_and_carries_label_expiry_and_thumbprint()
    {
        var pem = Pem("card");
        var answer = ReaderCertificateAnswer.Answered([new ReaderCertificate(pem, "YubiKey 5C", DateTimeOffset.UtcNow.AddYears(1))]);

        var described = ReaderCertificatesController.Describe(answer);

        Assert.Equal(("module", (string?)null), (described.Source, described.Reason));
        var entry = Assert.Single(described.Certificates);
        Assert.Equal("YubiKey 5C", entry.Label);
        using var certificate = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), entry.ThumbprintSha256);
    }

    [Theory]
    [InlineData("answered-empty", "noneEnrolled")]
    [InlineData("lapsed", "licenceLapsed")]
    [InlineData("failed", "moduleFailed")]
    public void An_empty_module_answer_says_why(string outcome, string reason)
    {
        var answer = outcome switch
        {
            "lapsed" => ReaderCertificateAnswer.LicenceLapsed,
            "failed" => ReaderCertificateAnswer.AskFailed,
            _ => ReaderCertificateAnswer.Answered([]),
        };

        var described = ReaderCertificatesController.Describe(answer);

        Assert.Equal(("module", reason), (described.Source, described.Reason));
        Assert.Empty(described.Certificates);
    }

    // #1970 slice 4: without a module the core's registry answers; the user's own column is no longer a source.
    [Fact]
    public void Without_a_module_the_registry_answers_or_nothing_does()
    {
        var registry = ReaderCertificatesController.Describe(
            ReaderCertificateAnswer.FromRegistry([new ReaderCertificate(Pem("Anna"), "Anna's key", DateTimeOffset.UtcNow.AddYears(1))]));
        Assert.Equal(("registry", "Anna's key"), (registry.Source, Assert.Single(registry.Certificates).Label));

        var none = ReaderCertificatesController.Describe(ReaderCertificateAnswer.FromRegistry([]));
        Assert.Equal(("none", "noneEnrolled"), (none.Source, none.Reason));
    }
}
