using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Alcifra.Infrastructure.Certificates.Attestation;

/// <summary>
/// The attestation roots the project ships (#1790, ADR 0963): those of the hardware it tests with, embedded as the
/// manufacturer publishes them, seeded switched off. Plus the manufacturer's published PIV intermediates, which only help
/// build a chain and are never trusted on their own.
/// </summary>
public static class AttestationCatalogue
{
    /// <summary>
    /// The catalogue key a DEVELOPMENT test root is seeded under (<see cref="TestRootPemKey"/>). Its private key is known
    /// to whoever generated it, so an installation outside Development that trusts it refuses to start.
    /// </summary>
    public const string TestRootKey = "development-test-root";

    /// <summary>Configuration naming a development test root (PEM), seeded under <see cref="TestRootKey"/> in Development only.</summary>
    public const string TestRootPemKey = "Attestation:DevelopmentTestRootPem";

    /// <summary>One shipped root: its stable catalogue key and its embedded resource.</summary>
    public sealed record Entry(string Key, string Resource);

    /// <summary>The shipped roots, in the order they are listed.</summary>
    public static IReadOnlyList<Entry> Roots { get; } =
    [
        // The PIV attestation root the manufacturer has signed tokens' attestation certificates with since 2016.
        new("piv-attestation-root-2016", "piv-root-2016"),
        // Its successor hierarchy (2024), which newer firmware chains to through the published intermediates.
        new("attestation-root-2024", "attestation-root-2024"),
    ];

    private static readonly Lazy<IReadOnlyList<X509Certificate2>> LazyIntermediates = new(() =>
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(Pem("piv-intermediates"));
        return [.. collection];
    });

    /// <summary>The manufacturer's published PIV intermediates: chain-building help, never anchors.</summary>
    public static IReadOnlyList<X509Certificate2> Intermediates => LazyIntermediates.Value;

    /// <summary>An embedded catalogue certificate's PEM.</summary>
    public static string Pem(string resource)
    {
        using var stream = typeof(AttestationCatalogue).Assembly.GetManifestResourceStream($"Alcifra.Attestation.{resource}.pem")
            ?? throw new InvalidOperationException($"The attestation catalogue resource '{resource}' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>SHA-256 of a certificate, uppercase hex: how a root is identified.</summary>
    public static string Thumbprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
