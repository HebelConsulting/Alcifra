using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.Inspection;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class InspectSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("The sealed file: an encrypted or signed .p7m, an S/MIME message, a sealed archive, a password entry's content.")]
    public string File { get; init; } = string.Empty;

    [CommandOption("--certs <FILE_OR_FOLDER>")]
    [Description("Certificates to match the recipients against (PEM or DER; a folder is read whole). Unmatched recipients are listed as unknown.")]
    public string? Certificates { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Per recipient: how the key reaches it, the algorithms, the reference, the matched certificate's validity.")]
    public bool Verbose { get; init; }

    [CommandOption("--extra-verbose")]
    [Description("Everything --verbose shows, and the full ASN.1 tree with offsets and lengths.")]
    public bool ExtraVerbose { get; init; }

    [CommandOption("--json")]
    [Description("The inspection as JSON on stdout (at the level asked for), for a script.")]
    public bool Json { get; init; }
}

/// <summary>
/// What a sealed file is and who can open it (#2007, slice 1: local files): read without a key and without decrypting
/// anything, its recipients named by certificate reference and matched to the certificates given with <c>--certs</c>.
/// Matching against the installation's enrolled certificates, documents and the audience gap follow.
/// </summary>
public sealed class InspectCommand(ILogger<InspectCommand> logger) : Command<InspectSettings>
{
    protected override int Execute(CommandContext context, InspectSettings settings, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            logger.LogError("No such file: {File}", settings.File);
            return 1;
        }

        var inspection = SealedInspector.Inspect(System.IO.File.ReadAllBytes(settings.File));
        var certificates = settings.Certificates is { } source ? InspectSurface.LoadCertificates(source) : [];
        var matched = RecipientMatching.Match(inspection.Recipients, certificates);
        var verbose = settings.Verbose || settings.ExtraVerbose;
        var tree = settings.ExtraVerbose && inspection.Cms is { } cms ? Asn1Tree.Of(cms) : null;

        if (settings.Json)
        {
            // Data output (ADR 0906): the inspection as JSON, the result a script captures; nothing else goes to stdout.
            Console.Out.WriteLine(JsonSerializer.Serialize(InspectSurface.Json(inspection, matched, verbose, tree), InspectSurface.JsonOptions));
            return 0;
        }

        logger.LogInformation("{Inspection}", InspectSurface.Describe(inspection, matched, verbose, tree));
        return inspection.Kind == SealedKind.NotSealed ? 2 : 0;
    }
}

public static class InspectSurface
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Every certificate in a file (PEM, several allowed, or DER) or in a folder's files; unreadable files are skipped.</summary>
    public static IReadOnlyList<X509Certificate2> LoadCertificates(string source)
    {
        var files = Directory.Exists(source) ? Directory.GetFiles(source) : [source];
        var found = new List<X509Certificate2>();
        foreach (var file in files)
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(file);
                if (Encoding.ASCII.GetString(bytes).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                {
                    var collection = new X509Certificate2Collection();
                    collection.ImportFromPem(Encoding.ASCII.GetString(bytes));
                    found.AddRange(collection);
                }
                else
                {
                    found.Add(X509CertificateLoader.LoadCertificate(bytes));
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Not a certificate: a folder may hold other files.
            }
        }

        return found;
    }

    /// <summary>The inspection as a person reads it: the default level, or with <paramref name="verbose"/> the details.</summary>
    public static string Describe(SealedInspection inspection, IReadOnlyList<MatchedRecipient> matched, bool verbose, IReadOnlyList<Asn1Node>? tree)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Format:     {inspection.Format}{(inspection.SmimeWrapped ? " (in an S/MIME message)" : string.Empty)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Size:       {inspection.Size} bytes");
        if (inspection.ContentType is { } contentType)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Content:    {contentType}");
        }

        if (inspection.ContentEncryption is { } encryption)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Encryption: {encryption}");
        }

        if (inspection.Archive is { } archive)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Archive:    \"{archive.Name}\", exported {archive.ExportedAt:u} from {archive.Source}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Signature:  {(archive.SignatureValid ? "holds" : "does NOT hold")}, signed by {archive.Signer ?? "nobody named"} (whether to trust the signer is the reader's decision)");
            foreach (var label in archive.Labels)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  labelled recipient: {label.Label} ({label.Kind}), certificate {label.Thumbprint}, valid until {label.NotAfter:yyyy-MM-dd}");
            }
        }

        if (inspection.Kind is SealedKind.Enveloped or SealedKind.Archive)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Recipients: {matched.Count}");
            foreach (var recipient in matched)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {Who(recipient)}");
                if (verbose)
                {
                    var r = recipient.Recipient;
                    text.AppendLine(CultureInfo.InvariantCulture, $"      {r.Method}, key wrapped with {r.KeyEncryptionAlgorithm}{(r.OriginatorKeyAlgorithm is { } o ? $", sender's ephemeral key {o}" : string.Empty)}");
                    text.AppendLine(CultureInfo.InvariantCulture, $"      reference: {Reference(r)}");
                    if (recipient.Certificate is { } c)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"      certificate valid {c.NotBefore:yyyy-MM-dd} to {c.NotAfter:yyyy-MM-dd}, thumbprint {c.Thumbprint}");
                    }
                }
            }
        }

        if (inspection.Kind == SealedKind.AuthEnveloped)
        {
            text.AppendLine("Recipients: not decoded for AuthEnvelopedData; --extra-verbose shows the structure.");
        }

        if (inspection.Kind == SealedKind.Signed)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Signers:    {inspection.Signers.Count}");
            foreach (var signer in inspection.Signers)
            {
                var state = signer.SignatureValid switch { true => "signature holds", false => "signature does NOT hold", null => "detached: check it with the signed file" };
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {signer.Subject ?? "unnamed signer"}: {state}");
                if (verbose)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"      digest {signer.DigestAlgorithm}{(signer.SigningTime is { } t ? $", signed {t:u}" : string.Empty)}, issuer {signer.Issuer ?? "?"}, serial {signer.Serial ?? "?"}");
                }
            }

            foreach (var certificate in inspection.Certificates)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  carries certificate: {certificate}");
            }
        }

        if (tree is not null)
        {
            text.AppendLine("ASN.1:");
            text.AppendLine(Asn1Tree.Lines(tree));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The inspection as data, at the level asked for.</summary>
    public static object Json(SealedInspection inspection, IReadOnlyList<MatchedRecipient> matched, bool verbose, IReadOnlyList<Asn1Node>? tree) => new
    {
        format = inspection.Format,
        kind = inspection.Kind.ToString(),
        smimeWrapped = inspection.SmimeWrapped,
        size = inspection.Size,
        contentType = inspection.ContentType,
        contentEncryption = inspection.ContentEncryption,
        recipients = matched.Select(m => new
        {
            matched = m.Certificate?.Subject,
            issuer = m.Recipient.Reference.Issuer,
            serial = m.Recipient.Reference.Serial,
            subjectKeyIdentifier = m.Recipient.Reference.SubjectKeyIdentifier,
            method = verbose ? m.Recipient.Method : null,
            keyEncryptionAlgorithm = verbose ? m.Recipient.KeyEncryptionAlgorithm : null,
            originatorKeyAlgorithm = verbose ? m.Recipient.OriginatorKeyAlgorithm : null,
            notBefore = verbose ? m.Certificate?.NotBefore : null,
            notAfter = verbose ? m.Certificate?.NotAfter : null,
        }),
        signers = inspection.Signers,
        certificates = inspection.Certificates,
        archive = inspection.Archive,
        asn1 = tree,
    };

    private static string Who(MatchedRecipient recipient) => recipient.Certificate is { } c
        ? $"{c.GetNameInfo(X509NameType.SimpleName, forIssuer: false)}{(c.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } email ? $" <{email}>" : string.Empty)}"
        : $"unknown recipient ({Reference(recipient.Recipient)})";

    private static string Reference(InspectedRecipient recipient) => recipient.Reference switch
    {
        { SubjectKeyIdentifier: { } ski } => $"subject key identifier {ski}",
        { Issuer: { } issuer, Serial: { } serial } => $"issuer {issuer}, serial {serial}",
        _ => "no reference",
    };
}
