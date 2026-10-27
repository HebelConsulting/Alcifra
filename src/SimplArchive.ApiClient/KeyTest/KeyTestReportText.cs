using System.Globalization;
using System.Text;

namespace SimplArchive.ApiClient.KeyTest;

/// <summary>
/// The plain-text form of a <see cref="KeyTestReport"/> — what the Copy button puts on the clipboard (ADR 0955).
/// </summary>
/// <remarks>
/// <para>
/// <b>English on purpose, whatever the app's language.</b> The text is written for support to read, and one wording
/// across languages and both clients is what lets a report be compared with the last one; the window around it is
/// translated, the artefact is not. Dates are UTC and invariant for the same reason.
/// </para>
/// <para>
/// It ends by saying what it does NOT contain, so the person pasting it knows before they send it.
/// </para>
/// </remarks>
public static class KeyTestReportText
{
    /// <summary>What a field the server does not advertise reads as.</summary>
    public const string NotAdvertised = "not advertised by this server";

    /// <summary>The closing line, also shown by a client next to the Copy button.</summary>
    public const string NeverContains =
        "This report never contains the PIN, document content, sign-in tokens or other secrets.";

    /// <summary>Renders the report; <paramref name="includeArchive"/> is the window's switch for the archive side.</summary>
    public static string Render(KeyTestReport report, bool includeArchive = true)
    {
        var text = new StringBuilder();
        Line(text, $"{SimplArchive.Localization.Strings.ProductName} hardware key test, {Stamp(report.At)}");
        Line(text, $"Result: {Verdict(report)}");

        Section(text, "Environment");
        var environment = report.Environment;
        Field(text, "App", $"{environment.App} {environment.AppVersion}");
        Field(text, "Operating system", environment.OperatingSystem);
        Field(text, "Architecture", environment.Architecture);
        if (environment.Library is { } library)
        {
            Field(text, "PKCS#11 library", library.Path);
            Field(text, "  version", library.Version ?? "not read");
            if (library.Manufacturer is { Length: > 0 } || library.Description is { Length: > 0 })
            {
                Field(text, "  says it is", string.Join(", ", new[] { library.Manufacturer, library.Description }
                    .Where(s => s is { Length: > 0 })));
            }

            if (library.InterfaceVersion is { Length: > 0 } cryptoki)
            {
                Field(text, "  PKCS#11 interface", cryptoki);
            }
        }
        else if (environment.Connection is { Length: > 0 } connection)
        {
            Field(text, "Connection", connection);
        }
        else
        {
            Field(text, "PKCS#11 library", "none found");
        }

        Field(text, "Readers", environment.Readers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var reader in environment.Readers)
        {
            Line(text, $"  - {reader.Description} ({(reader.KeyPresent ? "key present" : "empty")})");
        }

        Section(text, "The key");
        if (report.Keys.Count == 0)
        {
            Line(text, "No key was seen.");
        }

        for (var i = 0; i < report.Keys.Count; i++)
        {
            var key = report.Keys[i];
            Line(text, $"Key {i + 1}: {key.Label}");
            Field(text, "  Manufacturer", key.Manufacturer);
            Field(text, "  Model", key.Model);
            Field(text, "  Firmware", key.Firmware);
            Field(text, "  Serial number", key.SerialNumber);
            Field(text, "  Slots with keys", key.KeySlots.Count == 0 ? "none shown before the PIN" : string.Join("; ", key.KeySlots));
            if (key.Certificates.Count == 0)
            {
                Line(text, "  No certificate on this key.");
            }

            foreach (var certificate in key.Certificates)
            {
                Line(text, $"  Certificate \"{certificate.Label}\"{(certificate.Tested ? " (tested)" : string.Empty)}");
                Field(text, "    Subject", certificate.Subject);
                Field(text, "    Issuer", certificate.Issuer);
                Field(text, "    Key", certificate.KeyType);
                Field(text, "    Valid", $"{Stamp(certificate.NotBefore)} to {Stamp(certificate.NotAfter)}");
                Field(text, "    SHA-256", certificate.ThumbprintSha256);
            }
        }

        Section(text, "The test");
        for (var i = 0; i < report.Steps.Count; i++)
        {
            var step = report.Steps[i];
            var on = step.Key is { Length: > 0 } key ? $" [{key}]" : string.Empty;
            Line(text, $"{i + 1}. {StepName(step.Kind)}{on}: {OutcomeName(step.Outcome)}");
            if (step.Finding.Length > 0)
            {
                Line(text, $"   {step.Finding}");
            }

            if (step.ErrorCode is { Length: > 0 } code)
            {
                Line(text, $"   code: {code}");
            }
        }

        Section(text, "Archive side");
        if (!includeArchive)
        {
            Line(text, "Left out by the user.");
        }
        else if (report.Archive is not { } archive)
        {
            Line(text, "Not signed in.");
        }
        else
        {
            Field(text, "Server address", archive.ServerAddress);
            Field(text, "Server version", archive.ServerVersion ?? NotAdvertised);
            Field(text, "Enrolled for you", Enrolment(archive));
            Field(text, "Encryption mode", archive.Encryption is { } encryption ? Encryption(encryption) : NotAdvertised);
            Field(text, "PIN policy", PinPolicy(archive));
            foreach (var problem in archive.Problems)
            {
                Line(text, $"  could not read: {problem}");
            }
        }

        text.Append('\n');
        Line(text, NeverContains);
        return text.ToString();
    }

    /// <summary>The step's name as the report prints it.</summary>
    public static string StepName(KeyTestStepKind kind) => kind switch
    {
        KeyTestStepKind.FindLibrary => "Find the PKCS#11 library",
        KeyTestStepKind.ListReaders => "List the readers",
        KeyTestStepKind.Connect => "Connect to the key",
        KeyTestStepKind.SelectPiv => "Select the PIV application",
        KeyTestStepKind.ReadKey => "Read the key and its certificates",
        KeyTestStepKind.ChooseCertificate => "Choose the key-management certificate",
        KeyTestStepKind.Seal => "Seal a fresh random message to it",
        KeyTestStepKind.Unlock => "Unlock the key with its PIN",
        KeyTestStepKind.Open => "Open the message with the key",
        KeyTestStepKind.Compare => "Compare with what was sealed",
        _ => kind.ToString(),
    };

    private static string OutcomeName(KeyTestOutcome outcome) => outcome switch
    {
        KeyTestOutcome.Passed => "passed",
        KeyTestOutcome.Failed => "FAILED",
        _ => "skipped",
    };

    private static string Verdict(KeyTestReport report) => report.Verdict switch
    {
        KeyTestVerdict.Works => "WORKS (the key opened a message sealed to its own certificate)",
        KeyTestVerdict.Failed => $"FAILED at \"{StepName(report.FirstFailure!.Kind)}\"",
        _ => "NOT COMPLETED",
    };

    private static string Enrolment(KeyTestArchiveSide archive)
    {
        if (archive.Enrolments is not { } enrolments)
        {
            return NotAdvertised;
        }

        var source = archive.CertificateSetSource is { Length: > 0 } s ? $" (certificate set: {s}" : " (certificate set: unknown";
        var reason = archive.CertificateSetReason is { Length: > 0 } r ? $", {r})" : ")";
        return enrolments.Count == 0
            ? $"no certificate was tested{source}{reason}"
            : $"{string.Join("; ", enrolments.Select(e => $"{(e.Enrolled ? "yes" : "NO")}, {Short(e.ThumbprintSha256)}"))}{source}{reason}";
    }

    private static string Encryption(KeyTestEncryption encryption) =>
        $"{encryption.Mode} (wraps at rest: {YesNo(encryption.WrapsAtRest)}; refuses plaintext doors: "
        + $"{YesNo(encryption.RefusesPlaintextDoors)}; delivers envelopes: {YesNo(encryption.DeliversEnvelopes)}; "
        + $"envelopes mail: {YesNo(encryption.EnvelopesMail)})";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string PinPolicy(KeyTestArchiveSide archive) => archive.PinOncePerSession switch
    {
        null => NotAdvertised,
        true => $"a phone may hold the PIN for a session (offline unlock: {archive.OfflineUnlockMode ?? "unknown"})",
        false => $"the PIN is asked for every decryption (offline unlock: {archive.OfflineUnlockMode ?? "unknown"})",
    };

    private static string Short(string thumbprint) => thumbprint.Length > 16 ? $"SHA-256 {thumbprint[..16]}…" : thumbprint;

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static void Section(StringBuilder text, string title) => text.Append('\n').Append("== ").Append(title).Append(" ==\n");

    private static void Field(StringBuilder text, string name, string value) =>
        text.Append(name.PadRight(22)).Append(": ").Append(value).Append('\n');

    private static void Line(StringBuilder text, string line) => text.Append(line).Append('\n');
}
