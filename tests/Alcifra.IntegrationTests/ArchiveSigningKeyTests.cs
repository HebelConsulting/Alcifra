using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Alcifra.Api.Errors.Exceptions.OfflineArchives;
using Alcifra.Api.OfflineArchives;

namespace Alcifra.IntegrationTests;

// The offline-archive signing key (ADR 0961): the configured key signs and leads the history, retired keys stay
// published after it; Development without one generates an ephemeral key; production without one refuses BY NAME.
public class ArchiveSigningKeyTests
{
    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Alcifra.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (string Certificate, string Key) Pem(string name)
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static ArchiveSigningKey Key(ArchiveSigningOptions options, string environment) =>
        new(Options.Create(options), new StubEnvironment(environment), NullLogger<ArchiveSigningKey>.Instance);

    [Fact]
    public void The_configured_key_signs_and_leads_the_published_history_with_the_retired_keys_after_it()
    {
        var current = Pem("Current");
        var (retiredOne, _) = Pem("Retired one");
        var (retiredTwo, _) = Pem("Retired two");

        var signing = Key(new ArchiveSigningOptions
        {
            SigningCertificatePem = current.Certificate,
            SigningKeyPem = current.Key,
            PreviousSigningCertificatesPem = retiredOne + retiredTwo,
        }, Environments.Production);

        Assert.True(signing.Require().HasPrivateKey);
        Assert.Equal(["CN=Current", "CN=Retired one", "CN=Retired two"], signing.History.Select(k => k.Subject));
        Assert.All(signing.History, k => Assert.False(k.HasPrivateKey));
        var published = signing.HistoryPem();
        Assert.Contains("(current: it signs)", published, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", published, StringComparison.Ordinal);
        var parsed = new X509Certificate2Collection();
        parsed.ImportFromPem(published);   // the whole document is PEM input, comment lines and all
        Assert.Equal(3, parsed.Count);
    }

    [Fact]
    public void Development_without_a_key_signs_with_an_ephemeral_one()
    {
        var signing = Key(new ArchiveSigningOptions(), Environments.Development);

        Assert.True(signing.Require().HasPrivateKey);
        Assert.Single(signing.History);
    }

    [Fact]
    public void Production_without_a_key_refuses_by_name_and_publishes_only_what_retired()
    {
        var (retired, _) = Pem("Retired");

        var signing = Key(new ArchiveSigningOptions { PreviousSigningCertificatesPem = retired }, Environments.Production);

        Assert.Null(signing.Current);
        Assert.Throws<ArchiveSigningKeyMissingException>(() => signing.Require());
        Assert.Equal(["CN=Retired"], signing.History.Select(k => k.Subject));
    }
}
