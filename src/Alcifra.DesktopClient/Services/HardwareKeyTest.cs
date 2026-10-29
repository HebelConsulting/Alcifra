using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using CAManagement.Pkcs11;
using Alcifra.ApiClient.KeyTest;
using SimplArchive.Messenger.Client;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// <i>Test the hardware key</i>: seals a fresh random message to the key's own certificate, opens it with the key and
/// its PIN, and compares — and reports every step in the shared report shape (core #1864, ADR 0955).
/// </summary>
/// <remarks>
/// <para>
/// <b>It seals to the key itself</b>, as the phone's test does (Mobile ADR 0021): a fixed test envelope is addressed
/// to one key, so every other key fails it by construction, and customers test with their own. Sealing is the
/// Messenger client's <c>ConversationKeys.SealTo</c> (RSA key transport, or elliptic-curve key agreement on the
/// key's own curve) — one sealer, never a second. Opening is the document path's own key selection and routing
/// (<see cref="CardEnvelopeOpener.OpenOnCard"/>), so a key that passes here is a key documents open with.
/// </para>
/// <para>
/// <b>No server, no account.</b> It runs from the logon window as well as the main window. Signed in, the archive
/// side is added by the <c>archiveSide</c> constructor argument — what the surroundings can answer when the test is
/// built, so the logon window simply passes none (ADR 0730).
/// </para>
/// <para>
/// <b>The PIN is asked through <c>askPin</c>, used once, and registered as a secret with the report builder</b>, so
/// a library error that echoes it is withheld rather than copied to the clipboard.
/// </para>
/// </remarks>
/// <param name="port">The way to the key: PKCS#11 in production, a software key in tests.</param>
/// <param name="askPin">Asks for the named key's PIN; null or empty means the user declined.</param>
/// <param name="archiveSide">Reads the archive side for the tested certificates' thumbprints; null when signed out.</param>
/// <param name="appVersion">This client's version, for the report.</param>
public sealed class HardwareKeyTest(
    IHardwareKeyPort port,
    Func<CardBeingUnlocked, Task<string?>> askPin,
    Func<IReadOnlyList<string>, Task<KeyTestArchiveSide?>>? archiveSide,
    string appVersion)
{
    /// <summary>The name the report gives this app.</summary>
    public static string AppName => $"{Alcifra.Localization.Strings.ProductName} Desktop";

    /// <summary>Runs the test; <paramref name="stepped"/> sees each step as it is recorded.</summary>
    public async Task<KeyTestReport> RunAsync(Action<KeyTestStep>? stepped = null)
    {
        var report = new KeyTestReportBuilder();
        KeyTestLibrary? library = null;
        IReadOnlyList<KeyTestReader> readers = [];
        var tested = new List<string>();

        void Record(KeyTestStepKind kind, KeyTestOutcome outcome, string detail, string? key = null, string? code = null)
        {
            // Recorded FIRST, then shown: `stepped?.Invoke(report.Step(...))` skips the argument too when nobody
            // watches, which recorded no step at all.
            var step = report.Step(kind, outcome, detail, key, code);
            stepped?.Invoke(step);
        }

        void Failed(KeyTestStepKind kind, string detail, Exception e, string? key = null) =>
            Record(kind, KeyTestOutcome.Failed, $"{detail} {Reason(e)}", key, Code(e));

        await RunStepsAsync();

        if (archiveSide is not null)
        {
            try
            {
                report.Archive(await archiveSide(tested));
            }
            catch (Exception e)
            {
                // Best-effort context, never a failed test (the probe already guards each read).
                DesktopLog.Warn(e, "The hardware-key report could not read the archive side");
            }
        }

        report.Environment(KeyTestEnvironment.ThisMachine(AppName, appVersion, library, readers));
        return report.Build();

        async Task RunStepsAsync()
        {
            try
            {
                library = await port.FindLibraryAsync();
            }
            catch (Exception e)
            {
                Failed(KeyTestStepKind.FindLibrary, "The PKCS#11 library was found but could not be loaded.", e);
                return;
            }

            if (library is null)
            {
                Record(KeyTestStepKind.FindLibrary, KeyTestOutcome.Failed,
                    "No PKCS#11 library was found where the key's software is usually installed. Install the "
                    + "smartcard software (OpenSC, or the key maker's PKCS#11 library) and run the test again.",
                    code: "NO_LIBRARY");
                return;
            }

            Record(KeyTestStepKind.FindLibrary, KeyTestOutcome.Passed, library.Path);

            KeyPortSurvey survey;
            try
            {
                survey = await port.SurveyAsync();
            }
            catch (Exception e)
            {
                Failed(KeyTestStepKind.ListReaders, "The library could not list the readers.", e);
                return;
            }

            readers = survey.Readers;
            if (readers.Count == 0)
            {
                Record(KeyTestStepKind.ListReaders, KeyTestOutcome.Failed,
                    "The library sees no reader. Plug the key in (or connect the card reader) and run the test again.",
                    code: "NO_READER");
                return;
            }

            Record(KeyTestStepKind.ListReaders, KeyTestOutcome.Passed,
                $"{readers.Count} reader(s), {readers.Count(r => r.KeyPresent)} with a key in it.");

            foreach (var (name, failure) in survey.Failures)
            {
                Failed(KeyTestStepKind.ReadKey, "This key could not be read.", failure, name);
            }

            if (survey.Keys.Count == 0)
            {
                if (survey.Failures.Count == 0)
                {
                    Record(KeyTestStepKind.ReadKey, KeyTestOutcome.Failed,
                        "No key is in a reader. Insert the key and run the test again.", code: "NO_KEY");
                }

                return;
            }

            foreach (var key in survey.Keys)
            {
                var chosen = ChooseKeyManagement(key.Certificates);
                if (chosen is not null)
                {
                    tested.Add(KeyTestCertificate.ThumbprintOf(chosen.Certificate));
                }

                report.Key(Describe(key, chosen));
                await TestOneAsync(key, chosen);
            }
        }

        async Task TestOneAsync(KeyPortKey key, CardCertificates.Found? chosen)
        {
            var name = $"{key.Label}, serial {key.SerialNumber}";
            Record(KeyTestStepKind.ReadKey, KeyTestOutcome.Passed, $"{key.Certificates.Count} certificate(s) on the key.", name);

            if (chosen is null)
            {
                Record(KeyTestStepKind.ChooseCertificate, KeyTestOutcome.Failed,
                    "This key holds no certificate, so nothing can be sealed to it. A certificate belongs in its "
                    + "key-management slot (9D on a PIV key).", name, "NO_CERTIFICATE");
                return;
            }

            var keyType = KeyTestCertificate.KeyTypeOf(chosen.Certificate);
            Record(KeyTestStepKind.ChooseCertificate, KeyTestOutcome.Passed, $"\"{chosen.ObjectLabel}\", {keyType}.", name);

            var message = RandomNumberGenerator.GetBytes(32);
            byte[] sealedMessage;
            try
            {
                sealedMessage = ConversationKeys.SealTo(message, [chosen.Certificate]);
            }
            catch (Exception e)
            {
                Failed(KeyTestStepKind.Seal, $"A message cannot be sealed to this {keyType} certificate.", e, name);
                return;
            }

            Record(KeyTestStepKind.Seal, KeyTestOutcome.Passed, $"{RecipientKind(sealedMessage)}, {message.Length} random bytes.", name);

            var pin = await askPin(new CardBeingUnlocked(key.Label, key.SerialNumber, chosen.ObjectLabel));
            if (pin is not { Length: > 0 })
            {
                Record(KeyTestStepKind.Unlock, KeyTestOutcome.Skipped, "No PIN was given, so the key was not used.", name);
                return;
            }

            report.NeverShow(pin);
            try
            {
                byte[] opened;
                try
                {
                    opened = await port.OpenAsync(key, chosen, sealedMessage, pin);
                }
                catch (Exception e) when (e is KeyUnlockFailedException or KeyNotInReaderException)
                {
                    Failed(KeyTestStepKind.Unlock, UnlockAdvice(e), e, name);
                    return;
                }
                catch (Exception e)
                {
                    Record(KeyTestStepKind.Unlock, KeyTestOutcome.Passed, "The PIN was accepted.", name);
                    Failed(KeyTestStepKind.Open, "The key did not open the message.", e, name);
                    return;
                }

                Record(KeyTestStepKind.Unlock, KeyTestOutcome.Passed, "The PIN was accepted.", name);
                Record(KeyTestStepKind.Open, KeyTestOutcome.Passed, $"{opened.Length} bytes opened.", name);
                Record(KeyTestStepKind.Compare,
                    opened.AsSpan().SequenceEqual(message) ? KeyTestOutcome.Passed : KeyTestOutcome.Failed,
                    opened.AsSpan().SequenceEqual(message)
                        ? "What the key opened is exactly what was sealed."
                        : "What the key opened differs from what was sealed.",
                    name,
                    opened.AsSpan().SequenceEqual(message) ? null : "OPENED_DIFFERENT");
            }
            finally
            {
                report.ForgetSecrets();
            }
        }
    }

    /// <summary>
    /// The certificate to seal to: the one in the key-management slot, by its label as the PKCS#11 libraries name it
    /// (OpenSC: <c>Certificate for Key Management</c>; the key maker's: <c>X.509 Certificate for Key Management</c>);
    /// else the first whose key usage allows receiving an envelope; else the first; null when the key holds none.
    /// </summary>
    public static CardCertificates.Found? ChooseKeyManagement(IReadOnlyList<CardCertificates.Found> certificates) =>
        certificates.FirstOrDefault(c =>
            c.ObjectLabel.Contains("Key Management", StringComparison.OrdinalIgnoreCase)
            || c.ObjectLabel.Contains("KEY MAN", StringComparison.OrdinalIgnoreCase))
        ?? certificates.FirstOrDefault(c => CardCertificates.Concern(c.Certificate, DateTimeOffset.UtcNow) != CardCertificates.Concerns.KeyUsage)
        ?? certificates.FirstOrDefault();

    /// <summary>The exact code of a failure: a PKCS#11 return value with its operation, else the exception's type.</summary>
    public static string Code(Exception e) => e switch
    {
        Pkcs11Exception p => $"{p.ReturnValue} (0x{Convert.ToUInt64(p.ReturnValue):X8}) in {p.Operation}",
        HardwareKeyTestException { InnerException: { } inner } => Code(inner),
        KeyNotInReaderException => "KEY_NOT_IN_READER",
        HardwareKeyTestException => e.GetType().Name,
        CryptographicException c => $"{c.GetType().Name} (HRESULT 0x{c.HResult:X8})",
        _ => e.GetType().Name,
    };

    private static string Reason(Exception e) =>
        e is HardwareKeyTestException { InnerException: { } inner } ? $"{e.Message}: {inner.Message}" : e.Message;

    private static string UnlockAdvice(Exception e) =>
        (e.InnerException as Pkcs11Exception)?.ReturnValue.ToString() switch
        {
            "CKR_PIN_INCORRECT" => "The PIN was not accepted. Mind the retry counter: a few wrong entries block the PIN.",
            "CKR_PIN_LOCKED" => "The PIN is blocked. Unblock it with the PUK (or ask whoever issued the key).",
            "CKR_PIN_LEN_RANGE" => "The PIN has the wrong length for this key.",
            _ => "The key could not be unlocked.",
        };

    private static string RecipientKind(byte[] sealedMessage)
    {
        var envelope = new EnvelopedCms();
        envelope.Decode(sealedMessage);
        return envelope.RecipientInfos[0].Type == RecipientInfoType.KeyAgreement
            ? "Elliptic-curve key agreement"
            : "RSA key transport";
    }

    private static KeyTestKey Describe(KeyPortKey key, CardCertificates.Found? chosen) => new(
        key.Label,
        key.Manufacturer,
        key.Model,
        key.Firmware,
        key.SerialNumber,
        key.KeySlots,
        key.Certificates.Select(c => KeyTestCertificate.From(c.ObjectLabel, c.Certificate, ReferenceEquals(c, chosen))).ToList());
}

/// <summary>The way to a hardware key, for <see cref="HardwareKeyTest"/>: PKCS#11 in production, software in tests.</summary>
public interface IHardwareKeyPort
{
    /// <summary>The library found, or null when none is installed; throws when one is found and will not load.</summary>
    Task<KeyTestLibrary?> FindLibraryAsync();

    /// <summary>Every reader and every key present, with their certificates — public data, no PIN.</summary>
    Task<KeyPortSurvey> SurveyAsync();

    /// <summary>
    /// Unlocks <paramref name="key"/> with <paramref name="pin"/> and opens <paramref name="sealedMessage"/> for
    /// <paramref name="certificate"/>. Throws <see cref="KeyUnlockFailedException"/> or
    /// <see cref="KeyNotInReaderException"/> before the key was unlocked, anything else after.
    /// </summary>
    Task<byte[]> OpenAsync(KeyPortKey key, CardCertificates.Found certificate, byte[] sealedMessage, string pin);
}

/// <summary>What a survey of the readers found.</summary>
/// <param name="Failures">Keys that are present but could not be read, by name, with why.</param>
public sealed record KeyPortSurvey(
    IReadOnlyList<KeyTestReader> Readers,
    IReadOnlyList<KeyPortKey> Keys,
    IReadOnlyList<(string Key, Exception Failure)> Failures);

/// <summary>A key present in a reader, as it describes itself, with its certificates.</summary>
public sealed record KeyPortKey(
    string Label,
    string Manufacturer,
    string Model,
    string Firmware,
    string SerialNumber,
    IReadOnlyList<string> KeySlots,
    IReadOnlyList<CardCertificates.Found> Certificates);
