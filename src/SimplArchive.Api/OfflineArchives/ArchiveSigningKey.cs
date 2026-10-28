using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using SimplArchive.Api.Errors.Exceptions.OfflineArchives;

namespace SimplArchive.Api.OfflineArchives;

/// <summary>
/// The key that signs every offline archive's cover, and the history of every key that ever did (ADR 0961).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sourced as the OpenIddict certificates are</b> (ADR 0339): OpenBao when configured, else this configuration
/// section; in <b>Development</b> with neither, an EPHEMERAL key is generated in memory, so local runs and the test suites
/// exercise signing with nothing to set up. Such archives verify only while the process lives, which is why it is
/// confined to Development and said at Warning.
/// </para>
/// <para>
/// <b>Outside Development with no key, offline archives are simply refused</b> (<see cref="ArchiveSigningKeyMissingException"/>),
/// rather than the installation refusing to start: the archive is one feature, and an installation provisioned before
/// it keeps running. A key set by halves is refused at startup by the readiness gate.
/// </para>
/// <para>
/// <b>The history is the CURRENT key first, then every retired one</b>, as public certificates: published in the
/// "Archive signing keys" document and embedded in each archive, so an archive verifies years after its key retired.
/// </para>
/// </remarks>
public sealed class ArchiveSigningKey
{
    public ArchiveSigningKey(IOptions<ArchiveSigningOptions> options, IHostEnvironment environment, ILogger<ArchiveSigningKey> logger)
    {
        var configured = options.Value;
        var previous = Certificates(configured.PreviousSigningCertificatesPem);
        if (!string.IsNullOrWhiteSpace(configured.SigningCertificatePem) && !string.IsNullOrWhiteSpace(configured.SigningKeyPem))
        {
            using var fromPem = X509Certificate2.CreateFromPem(configured.SigningCertificatePem, configured.SigningKeyPem);
            // Round-tripped through PKCS#12 so the private key is reliably usable for signing on every platform
            // (the same reason OpenIddictCertificateLoader does it).
            Current = X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
            logger.LogInformation("Offline archives are signed with the key {Thumbprint}; {Previous} retired key(s) stay published.",
                Thumbprint(Current), previous.Count);
        }
        else if (environment.IsDevelopment())
        {
            Current = Ephemeral();
            logger.LogWarning("Offline archives are signed with an EPHEMERAL key {Thumbprint} (Development only): an archive signed now "
                + "does not verify after a restart.", Thumbprint(Current));
        }
        else
        {
            logger.LogInformation("Offline archives are disabled: no archive signing key is configured (OpenBao secret/simplarchive/archive-signing).");
        }

        History = Current is null ? previous : [X509CertificateLoader.LoadCertificate(Current.RawData), .. previous];
    }

    /// <summary>The signing certificate with its private key, or null when none is configured.</summary>
    public X509Certificate2? Current { get; }

    /// <summary>Every published signing key, newest (the current one) first, public halves only.</summary>
    public IReadOnlyList<X509Certificate2> History { get; }

    /// <summary>The signing certificate, or the refusal that names what is missing.</summary>
    public X509Certificate2 Require() => Current ?? throw new ArchiveSigningKeyMissingException();

    /// <summary>The history as one PEM text, with a comment line per key: the "Archive signing keys" document's content.</summary>
    public string HistoryPem() => string.Concat(History.Select((k, i) =>
        $"# Key {i + 1}{(i == 0 && Current is not null ? " (current: it signs)" : string.Empty)}, SHA-256 thumbprint {Thumbprint(k)}, "
        + $"valid {k.NotBefore.ToUniversalTime():yyyy-MM-dd} to {k.NotAfter.ToUniversalTime():yyyy-MM-dd}\n{k.ExportCertificatePem()}\n\n"));

    private static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static List<X509Certificate2> Certificates(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return [];
        }

        var collection = new X509Certificate2Collection();
        // Blocks pasted back to back ("-----END CERTIFICATE----------BEGIN…") would read as ONE: separate them first.
        collection.ImportFromPem(pem.Replace("-----END CERTIFICATE-----", "-----END CERTIFICATE-----\n", StringComparison.Ordinal));
        return [.. collection];
    }

    private static X509Certificate2 Ephemeral()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={SimplArchive.Localization.Strings.ProductName} archive signing - development ephemeral", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }
}
