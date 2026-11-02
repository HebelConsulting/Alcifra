using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class CertificateEnrolSettings : TenantSessionSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The certificate to enrol — PEM (.pem/.crt) or DER (.cer/.der); a chain is accepted. Or a CERTIFICATE REQUEST (PEM) the token made, signed by the certificate authority named with --ca first (ADR 1037).")]
    public required string File { get; init; }

    [CommandOption("--ca <url>")]
    [Description("The certificate authority that signs a CSR as a reader certificate; needed when <file> is a certificate request.")]
    public string? CertificateAuthority { get; init; }

    [CommandOption("--ca-secret-env <variable>")]
    [Description("The environment variable holding the authority's admin secret (typed by you, never on the command line).")]
    public string? CertificateAuthoritySecretVariable { get; init; }

    [CommandOption("--holder")]
    [Description("Whose certificate this is, by e-mail. Omitted enrols it for the caller.")]
    public string? Holder { get; init; }

    [CommandOption("--label")]
    [Description("What the holder calls this key: \"YubiKey 5C\", \"work laptop\".")]
    public string? Label { get; init; }

    [CommandOption("--attestation <file>")]
    [Description("The key slot's attestation certificate, as the token's tool exports it (ADR 0963). Required unless the tenant allows hardware without attestation.")]
    public string? Attestation { get; init; }

    [CommandOption("--attestation-intermediate <file>")]
    [Description("The token's attestation certificate that signed it (the PIV F9 certificate); repeat for further intermediates.")]
    public string[] AttestationIntermediates { get; init; } = [];

    [CommandOption("--slot <slot>")]
    [Description("The key slot the certificate is in, e.g. 9d; informational.")]
    public string? Slot { get; init; }
}

/// <summary>
/// Enrols ONE reader certificate (#1504) — the single-certificate sibling of <c>certificates import</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because enrolling one certificate should not require writing a manifest, and because this is what
/// <c>curl</c> was being used for — which is how the module's missing root links went unnoticed for weeks: a
/// hand-written <c>POST</c> reaches an endpoint no rel advertises, and nothing complains.
/// </para>
/// <para>
/// This is the READER certificate documents are addressed to, not the self-service mail certificate
/// <c>alcifra me certificate register</c> writes. Two stores; this is the one content delivery uses.
/// </para>
/// </remarks>
public sealed class CertificateEnrolCommand(ILogger<CertificateEnrolCommand> logger) : AsyncCommand<CertificateEnrolSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateEnrolSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.File))
        {
            throw new CliException($"No such file: {settings.File}");
        }

        var bytes = await File.ReadAllBytesAsync(settings.File, cancellationToken);
        var attestation = AttestationFiles.Read(settings.Attestation, settings.AttestationIntermediates, settings.Slot);

        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var hypermedia = new Hypermedia(api);
        var certificates = await CertificateSurface.AddressAsync(hypermedia, cancellationToken);

        string pem;
        if (CaSigning.IsCertificateRequest(Encoding.UTF8.GetString(bytes)))
        {
            // A CSR from a token: the authority signs it, the enrolment door below judges the attestation (ADR 1037).
            if (settings.CertificateAuthority is not { Length: > 0 } authority || settings.CertificateAuthoritySecretVariable is not { Length: > 0 } variable)
            {
                throw new CliException("A certificate request needs --ca <url> and --ca-secret-env <variable>: the authority that signs it.");
            }

            if (settings.Holder is not { Length: > 0 } holder)
            {
                throw new CliException("A certificate request needs --holder: the address the certificate is issued to.");
            }

            var secret = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(secret))
            {
                throw new CliException($"{variable} is not set.");
            }

            var me = await api.GetAsync(await hypermedia.RootHrefAsync("whoami", cancellationToken), cancellationToken);
            var tenantId = me.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String && Guid.TryParse(t.GetString(), out var id)
                ? id
                : throw new CliException("The session names no tenant; sign in to the tenant the certificate belongs to.");
            using var ca = CaSigning.Client(secret);
            var (signedPem, caSerial) = await CaSigning.SignAsync(ca, new Uri(authority.TrimEnd('/') + "/"), tenantId, Encoding.UTF8.GetString(bytes), holder, attestation, cancellationToken);
            logger.LogInformation("The certificate authority signed the request: serial {Serial}.", caSerial);
            pem = signedPem;
        }
        else
        {
            pem = PemOf(bytes, settings.File);
        }

        try
        {
            var enrolled = await api.PostAsync(
                certificates,
                new { certificatePem = pem, label = settings.Label ?? string.Empty, holder = settings.Holder, attestation },
                cancellationToken);

            var label = enrolled.TryGetProperty("label", out var l) ? l.GetString() : null;
            var serial = enrolled.TryGetProperty("serial", out var s) ? s.GetString() : null;

            var state = enrolled.TryGetProperty("attestation", out var a) ? a.GetString() : null;
            logger.LogInformation(
                "Enrolled {Certificate} (serial {Serial}), {Attestation}. {Effect}",
                label ?? "the certificate", serial is { Length: > 0 } ? serial : "none", state ?? "attestation not reported",
                enrolled.TryGetProperty("usable", out var usable) && usable.ValueKind == System.Text.Json.JsonValueKind.False
                    ? "It does NOT address its holder here (see alcifra certificates list)."
                    : "Content addressed to this reader is now enveloped to it as well.");

            return 0;
        }
        catch (CliException exception) when (exception.Message.Contains(
            "CERTIFICATE_ALREADY_ENROLLED", StringComparison.Ordinal))   // the core's code and the module's ENCRYPTION_ one
        {
            // NOT a failure worth a non-zero exit on its own terms — the state the caller asked for already
            // obtains. Reported as unchanged, with the server's own sentence, which says WHO holds it when
            // that is somebody else (#1494).
            logger.LogInformation("Unchanged: {Reason}", exception.Message);
            return 0;
        }
    }

    /// <summary>
    /// The file as PEM, refusing a private key before anything leaves this machine.
    /// </summary>
    /// <remarks>
    /// The server refuses key material too, and so does the module's own policy — but the useful moment to
    /// say "that is not what you want to hand over" is while the file is still in front of the person who
    /// chose it. DER is converted rather than refused: a certificate exported from a card tool is as likely
    /// to be one as the other, and refusing it would be refusing the format rather than the content.
    /// </remarks>
    internal static string PemOf(byte[] bytes, string path)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new CliException(
                $"{path} contains a PRIVATE KEY. Enrol the public certificate only — a private key filed into "
                + "the archive is readable by everyone who can read the document, and it is the one thing "
                + "that must never leave the card or keystore that generated it.");
        }

        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(bytes);
            return certificate.ExportCertificatePem();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new CliException($"{path} is neither a PEM nor a DER certificate.");
        }
    }
}
