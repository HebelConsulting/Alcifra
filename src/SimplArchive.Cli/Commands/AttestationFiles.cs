using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// A key's attestation as files (ADR 0963): the slot's attestation certificate and the token's attestation certificate
/// that signed it (plus any further intermediates), as a token's own tool exports them, PEM or DER.
/// </summary>
/// <remarks>
/// The wire shape is the enrolment request's <c>attestation</c> object. Read and checked here for what can be told before
/// anything is sent (readable certificates, no private key); whether the chain holds is the server's verdict.
/// </remarks>
public sealed class AttestationFiles
{
    public string Format { get; set; } = "piv";
    public string AttestationCertificatePem { get; set; } = string.Empty;
    public string IntermediateCertificatePem { get; set; } = string.Empty;
    public string? Slot { get; set; }

    /// <summary>The evidence from files, or null when no attestation file was named.</summary>
    public static AttestationFiles? Read(string? attestation, IReadOnlyList<string> intermediates, string? slot)
    {
        if (attestation is not { Length: > 0 })
        {
            return intermediates.Count > 0
                ? throw new CliException("--attestation-intermediate needs --attestation: the slot's attestation certificate is the evidence.")
                : null;
        }

        if (intermediates.Count == 0)
        {
            throw new CliException("--attestation needs --attestation-intermediate: the token's attestation certificate that signed it.");
        }

        return new AttestationFiles
        {
            AttestationCertificatePem = Pem(attestation),
            IntermediateCertificatePem = string.Concat(intermediates.Select(Pem)),
            Slot = slot,
        };
    }

    /// <summary>A file of certificates (PEM or DER) as PEM, refusing a private key.</summary>
    internal static string Pem(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"No such file: {path}");
        }

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new CliException($"{path} contains a PRIVATE KEY. An attestation is public certificates only.");
        }

        try
        {
            var collection = new X509Certificate2Collection();
            if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                collection.ImportFromPem(text);
            }
            else
            {
                collection.Add(X509CertificateLoader.LoadCertificate(bytes));
            }

            return string.Concat(collection.Select(c => $"{c.ExportCertificatePem()}\n"));
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new CliException($"{path} is neither a PEM nor a DER certificate.");
        }
    }
}
