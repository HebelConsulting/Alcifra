using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Inspection;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class InspectSettings : TenantSessionSettings
{
    [CommandArgument(0, "[FILE]")]
    [Description("The sealed file: an encrypted or signed .p7m, an S/MIME message, a sealed archive, a password entry's content.")]
    public string? File { get; init; }

    [CommandOption("--document <ID>")]
    [Description("A document instead of a file: its current version as stored, and whether its envelope matches its audience. Needs a session.")]
    public string? Document { get; init; }

    [CommandOption("--certs <FILE_OR_FOLDER>")]
    [Description("Certificates to match the recipients against (PEM or DER; a folder is read whole), after the installation's.")]
    public string? Certificates { get; init; }

    [CommandOption("--try-open")]
    [Description("Try your own key: a hardware key in a reader (its PIN is asked), or the one in --key. Says which key opens it; the content is never kept or written.")]
    public bool TryOpen { get; init; }

    [CommandOption("--key <FILE>")]
    [Description("With --try-open: a key on this computer, as a PEM file holding the certificate and its private key, or a PKCS#12 file.")]
    public string? Key { get; init; }

    [CommandOption("--offline")]
    [Description("Do not ask the installation even when signed in: match only against --certs.")]
    public bool Offline { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Per recipient: how the key reaches it, the algorithms, the reference, the matched certificate's validity.")]
    public bool Verbose { get; init; }

    [CommandOption("--extra-verbose")]
    [Description("Everything --verbose shows, and the full ASN.1 tree with offsets and lengths.")]
    public bool ExtraVerbose { get; init; }

    [CommandOption("--json")]
    [Description("The inspection as JSON on stdout (at the level asked for), for a script.")]
    public bool Json { get; init; }

    /// <summary>Signed in, and asked to use it.</summary>
    public bool UsesSession => !Offline && !string.IsNullOrEmpty(ResolvedToken) && !string.IsNullOrWhiteSpace(ResolvedUrl);

    // A local file needs no session; a document does.
    public override ValidationResult Validate() => (File, Document) switch
    {
        (null, null) => ValidationResult.Error("Name a FILE, or a document with --document <ID>."),
        ({ }, { }) => ValidationResult.Error("Name a FILE or --document, not both."),
        (null, { }) => base.Validate(),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// What a sealed file or a document is and who can open it (#2007, ADR 1029): read without a key and without decrypting
/// anything. Its recipients, named by certificate reference, are matched to the installation's enrolled certificates
/// when signed in (holder, label, revoked, attestation), then to the ones given with <c>--certs</c>; a document's
/// envelope is compared with its declared audience.
/// </summary>
public sealed class InspectCommand(ILogger<InspectCommand> logger) : AsyncCommand<InspectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken)
    {
        using var http = settings.UsesSession || settings.Document is not null ? TenantHttp.Client(settings) : null;
        var api = http is null ? null : new AlcifraApi(http);
        byte[] bytes;
        ContentAudienceInfo? audience = null;
        if (settings.Document is { } document)
        {
            (bytes, audience) = await InspectSurface.FetchDocumentAsync(http!, api!, document, cancellationToken);
        }
        else if (System.IO.File.Exists(settings.File))
        {
            bytes = await System.IO.File.ReadAllBytesAsync(settings.File, cancellationToken);
        }
        else
        {
            logger.LogError("No such file: {File}", settings.File);
            return 1;
        }

        var inspection = SealedInspector.Inspect(bytes);
        var known = api is not null && settings.UsesSession ? await InspectSurface.KnownCertificatesAsync(api, cancellationToken) : [];
        var local = settings.Certificates is { } source ? InspectSurface.LoadCertificates(source) : [];
        var rows = InspectSurface.Rows(inspection.Recipients, known, local);
        var gap = audience is null ? null : AudienceGap.Of(inspection.Recipients, audience);
        var verbose = settings.Verbose || settings.ExtraVerbose;
        var tree = settings.ExtraVerbose && inspection.Cms is { } cms ? Asn1Tree.Of(cms) : null;
        var opened = settings.TryOpen && inspection.Kind is SealedKind.Enveloped or SealedKind.Archive && inspection.Cms is { } envelope
            ? await InspectSurface.TryOpenAsync(envelope, inspection.Recipients, settings.Key, logger)
            : null;

        if (settings.Json)
        {
            // Data output (ADR 0906): the inspection as JSON, the result a script captures; nothing else goes to stdout.
            Console.Out.WriteLine(JsonSerializer.Serialize(InspectSurface.Json(inspection, rows, gap, verbose, tree, opened), InspectSurface.JsonOptions));
            return 0;
        }

        logger.LogInformation("{Inspection}", InspectSurface.Describe(inspection, rows, gap, verbose, tree, opened));
        return inspection.Kind == SealedKind.NotSealed ? 2 : 0;
    }
}

/// <summary>What `--try-open` found: the key that opened the envelope, or why none did.</summary>
public sealed record TryOpenResult(string? OpenedBy, string? Why);

/// <summary>A recipient, and who it was matched to: the installation's certificate first, else a local one, else nobody.</summary>
public sealed record RecipientRow(InspectedRecipient Recipient, KnownCertificate? Known, X509Certificate2? Local);

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

    /// <summary>
    /// A document's current version as stored, never decrypted, and its declared audience where it has one: reached by
    /// the root's <c>document</c> template, then its <c>versions</c> and <c>audience</c> rels (ADR 0543).
    /// </summary>
    public static async Task<(byte[] Content, ContentAudienceInfo? Audience)> FetchDocumentAsync(
        HttpClient http, AlcifraApi api, string documentId, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var document = (await hypermedia.RootHrefAsync("document", cancellationToken)).Replace("{id}", documentId, StringComparison.Ordinal);
        var links = await hypermedia.LinksOfAsync(document, cancellationToken);
        var versions = await api.GetAsync($"{Hypermedia.Href(links, "versions", $"Document {documentId}")}?limit=200", cancellationToken);
        if (versions.GetProperty("currentVersionId") is not { ValueKind: JsonValueKind.String } current)
        {
            throw new CliException($"Document {documentId} has no current version to inspect.");
        }

        var version = versions.GetProperty("versions").EnumerateArray().First(v => v.GetProperty("id").GetString() == current.GetString());
        var download = version.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()
            ?? throw new CliException($"Document {documentId}'s content is not offered to you.");

        // A presigned link is absolute and anonymous; an object wrapped at rest is handed out by the server's own door.
        byte[] content;
        if (Uri.TryCreate(download, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            using var anonymous = new HttpClient();
            content = await anonymous.GetByteArrayAsync(absolute, cancellationToken);
        }
        else
        {
            content = await http.GetByteArrayAsync(download, cancellationToken);
        }

        var audience = links.TryGetValue("audience", out var audienceHref)
            ? await new ContentAudienceClient(ApiCore.Over(http)).GetAsync(audienceHref, cancellationToken)
            : null;
        return (content, audience);
    }

    /// <summary>The installation's certificates as the caller may see them (an administrator every holder, a user their
    /// own); empty where the installation offers no enrolment.</summary>
    public static async Task<IReadOnlyList<KnownCertificate>> KnownCertificatesAsync(AlcifraApi api, CancellationToken cancellationToken)
    {
        string href;
        try
        {
            href = await CertificateSurface.AddressAsync(new Hypermedia(api), cancellationToken);
        }
        catch (CliException)
        {
            return [];
        }

        // One parse of the listing for the CLI and the desktop (#2291).
        return EnrolledCertificatesClient.Known(await api.GetAsync(href, cancellationToken));
    }

    /// <summary>Each recipient with the installation's certificate it names, else a local one.</summary>
    public static IReadOnlyList<RecipientRow> Rows(
        IReadOnlyList<InspectedRecipient> recipients, IReadOnlyList<KnownCertificate> known, IReadOnlyList<X509Certificate2> local) =>
        [.. recipients.Select(r => new RecipientRow(r, known.FirstOrDefault(k => k.Is(r.Reference)), local.FirstOrDefault(r.Reference.Is)))];

    /// <summary>The inspection as a person reads it: the default level, or with <paramref name="verbose"/> the details.</summary>
    public static string Describe(SealedInspection inspection, IReadOnlyList<RecipientRow> rows, AudienceGap? gap, bool verbose, IReadOnlyList<Asn1Node>? tree,
        TryOpenResult? opened = null)
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
            text.AppendLine(CultureInfo.InvariantCulture, $"Recipients: {rows.Count}");
            foreach (var row in rows)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {Who(row)}");
                if (verbose)
                {
                    var r = row.Recipient;
                    text.AppendLine(CultureInfo.InvariantCulture, $"      {r.Method}, key wrapped with {r.KeyEncryptionAlgorithm}{(r.OriginatorKeyAlgorithm is { } o ? $", sender's ephemeral key {o}" : string.Empty)}");
                    text.AppendLine(CultureInfo.InvariantCulture, $"      reference: {Reference(r)}");
                    if (row.Local is { } c)
                    {
                        text.AppendLine(CultureInfo.InvariantCulture, $"      certificate valid {c.NotBefore:yyyy-MM-dd} to {c.NotAfter:yyyy-MM-dd}, thumbprint {c.Thumbprint}");
                    }
                }
            }
        }

        if (gap is not null)
        {
            text.AppendLine(gap.None ? "Audience:   the envelope matches the declared audience" : "Audience:   the envelope does NOT match the declared audience");
            foreach (var missing in gap.Missing)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  missing: {missing.Person} (certificate {missing.Certificate.Thumbprint}) is in the audience but not in the envelope: it needs a re-seal");
            }

            foreach (var extra in gap.Extra)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  extra:   {Who(rows.First(r => r.Recipient == extra))} is no longer in the audience but can still open this version");
            }
        }

        if (opened is not null)
        {
            text.AppendLine(opened.OpenedBy is { } by
                ? $"Try-open:   opened with {by} (the content was discarded at once)"
                : $"Try-open:   none of your keys opens it ({opened.Why})");
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
    public static object Json(SealedInspection inspection, IReadOnlyList<RecipientRow> rows, AudienceGap? gap, bool verbose, IReadOnlyList<Asn1Node>? tree,
        TryOpenResult? opened = null) => new
        {
            tryOpen = opened is null ? null : new { opened = opened.OpenedBy is not null, openedBy = opened.OpenedBy, why = opened.Why },
            format = inspection.Format,
            kind = inspection.Kind.ToString(),
            smimeWrapped = inspection.SmimeWrapped,
            size = inspection.Size,
            contentType = inspection.ContentType,
            contentEncryption = inspection.ContentEncryption,
            recipients = rows.Select(r => new
            {
                holder = r.Known?.HolderName,
                holderEmail = r.Known?.HolderEmail,
                label = r.Known?.Label,
                revokedAt = r.Known?.RevokedAt,
                attestation = r.Known?.Attestation,
                matched = r.Local?.Subject,
                issuer = r.Recipient.Reference.Issuer,
                serial = r.Recipient.Reference.Serial,
                subjectKeyIdentifier = r.Recipient.Reference.SubjectKeyIdentifier,
                method = verbose ? r.Recipient.Method : null,
                keyEncryptionAlgorithm = verbose ? r.Recipient.KeyEncryptionAlgorithm : null,
                originatorKeyAlgorithm = verbose ? r.Recipient.OriginatorKeyAlgorithm : null,
            }),
            audience = gap is null ? null : new
            {
                matches = gap.None,
                missing = gap.Missing.Select(m => new { person = m.Person, thumbprint = m.Certificate.Thumbprint }),
                extra = gap.Extra.Select(e => new { issuer = e.Reference.Issuer, serial = e.Reference.Serial, subjectKeyIdentifier = e.Reference.SubjectKeyIdentifier }),
            },
            signers = inspection.Signers,
            certificates = inspection.Certificates,
            archive = inspection.Archive,
            asn1 = tree,
        };

    // The installation's holder first (with the key's label and what is wrong with it), else a local certificate's name.
    private static string Who(RecipientRow row) => row switch
    {
        { Known: { } k } => $"{k.HolderName ?? "a holder you may not see"}{(k.HolderEmail is { } e ? $" <{e}>" : string.Empty)}, key \"{k.Label}\""
            + (k.RevokedAt is { } revoked ? $", REVOKED since {revoked:yyyy-MM-dd} (exposed since then)" : string.Empty)
            + (k.Attestation == "attested" ? string.Empty : $", {k.Attestation}"),
        { Local: { } c } => $"{c.GetNameInfo(X509NameType.SimpleName, forIssuer: false)}{(c.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } email ? $" <{email}>" : string.Empty)}",
        _ => $"unknown recipient ({Reference(row.Recipient)})",
    };

    private static string Reference(InspectedRecipient recipient) => recipient.Reference switch
    {
        { SubjectKeyIdentifier: { } ski } => $"subject key identifier {ski}",
        { Issuer: { } issuer, Serial: { } serial } => $"issuer {issuer}, serial {serial}",
        _ => "no reference",
    };

    /// <summary>
    /// Which of the caller's keys opens the envelope (#2007 slice 3): the key in <paramref name="keyFile"/>, or else a
    /// hardware key in a reader (its PIN asked only if it holds a recipient's certificate). The content is wiped at once
    /// and never written; only which key opened it is said. The PIN is never logged (ADR 0886).
    /// </summary>
    public static async Task<TryOpenResult> TryOpenAsync(byte[] cms, IReadOnlyList<InspectedRecipient> recipients, string? keyFile, ILogger logger)
    {
        // The trying itself is shared with the desktop (Alcifra.HardwareKeys.KeyTry, #2291); the prompts and these
        // sentences are this command's.
        if (keyFile is not null)
        {
            using var key = LoadKey(keyFile, logger);
            var file = Path.GetFileName(keyFile);
            return Alcifra.HardwareKeys.KeyTry.WithKey(cms, recipients, key, message => logger.LogDebug("{Reason}", message)).Outcome switch
            {
                Alcifra.HardwareKeys.KeyTryOutcome.Opened => new TryOpenResult($"the key in {file} ({key.Subject})", null),
                Alcifra.HardwareKeys.KeyTryOutcome.NotAddressedToKey => new TryOpenResult(null, $"it is not addressed to {key.Subject}"),
                _ => new TryOpenResult(null, $"the key in {file} did not open it"),
            };
        }

        Alcifra.HardwareKeys.CardSession.PinPrompt = card =>
            Task.FromResult(Alcifra.ConsoleLogging.SecretInput.ReadHidden($"PIN for {card.TokenLabel} ({card.ObjectLabel}):", logger));
        try
        {
            var tried = await Alcifra.HardwareKeys.KeyTry.WithCardAsync(cms, recipients, _ => true);
            return tried.Outcome switch
            {
                Alcifra.HardwareKeys.KeyTryOutcome.Opened => new TryOpenResult($"the hardware key's certificate {tried.Key ?? "(unnamed)"}", null),
                Alcifra.HardwareKeys.KeyTryOutcome.NoCard => new TryOpenResult(null, tried.Detail),
                _ => new TryOpenResult(null, "the hardware key in the reader is not one it is sealed to"),
            };
        }
        finally
        {
            Alcifra.HardwareKeys.CardSession.Close();
        }
    }

    // A PEM file holding the certificate and its private key, or a PKCS#12 file (its password asked, never logged); the
    // loading is shared with the desktop (KeyTry.LoadKeyFile, #2291).
    internal static X509Certificate2 LoadKey(string path, ILogger logger)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        try
        {
            return Alcifra.HardwareKeys.KeyTry.LoadKeyFile(bytes, null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Alcifra.HardwareKeys.KeyTry.LoadKeyFile(bytes, Alcifra.ConsoleLogging.SecretInput.ReadHidden($"Password for {Path.GetFileName(path)}:", logger));
        }
    }
}
