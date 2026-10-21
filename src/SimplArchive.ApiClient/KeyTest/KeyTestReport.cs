using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.ApiClient.KeyTest;

/// <summary>
/// What a hardware-key test found, in the ONE shape the desktop and the phone both report (core #1864, ADR 0955).
/// </summary>
/// <remarks>
/// <para>
/// <b>One shape, so both clients say the same things the same way.</b> A customer sends us this text; support reads
/// it. Two clients composing their own reports would drift into two vocabularies for one key, which is the reading
/// cost this type exists to remove. Each client fills it from its own path to the key (the desktop's PKCS#11 library,
/// the phone's USB or NFC transport) and renders it with <see cref="KeyTestReportText"/>.
/// </para>
/// <para>
/// <b>What it can never hold</b> is decided by the builder, not by the callers: the PIN, any content, tokens and
/// secrets. See <see cref="KeyTestReportBuilder.NeverShow"/>.
/// </para>
/// </remarks>
/// <param name="At">When the test ran.</param>
/// <param name="Environment">The app, the operating system and the path to the key.</param>
/// <param name="Keys">Every key that was seen, with its certificates.</param>
/// <param name="Steps">The test's steps in order, each with its outcome.</param>
/// <param name="Archive">The archive side, or null when nobody is signed in.</param>
public sealed record KeyTestReport(
    DateTimeOffset At,
    KeyTestEnvironment Environment,
    IReadOnlyList<KeyTestKey> Keys,
    IReadOnlyList<KeyTestStep> Steps,
    KeyTestArchiveSide? Archive)
{
    /// <summary>
    /// Works when at least one key opened its own message and nothing failed; Failed when any step failed; otherwise
    /// the test did not get that far (the PIN was not given).
    /// </summary>
    public KeyTestVerdict Verdict =>
        Steps.Any(s => s.Outcome == KeyTestOutcome.Failed) ? KeyTestVerdict.Failed
        : Steps.Any(s => s is { Kind: KeyTestStepKind.Compare, Outcome: KeyTestOutcome.Passed }) ? KeyTestVerdict.Works
        : KeyTestVerdict.NotCompleted;

    /// <summary>The first failed step, or null — what a client names as "where it stopped".</summary>
    public KeyTestStep? FirstFailure => Steps.FirstOrDefault(s => s.Outcome == KeyTestOutcome.Failed);
}

/// <summary>The test's overall answer.</summary>
public enum KeyTestVerdict
{
    /// <summary>The key opened a message sealed to its own certificate.</summary>
    Works,

    /// <summary>A step failed; <see cref="KeyTestReport.FirstFailure"/> says which.</summary>
    Failed,

    /// <summary>Nothing failed, but the test did not reach the comparison (the PIN was not given).</summary>
    NotCompleted,
}

/// <summary>How one step ended.</summary>
public enum KeyTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>The steps of the test, in the order they run.</summary>
/// <remarks>Stable names: the renderer prints them, and support reads a report by them.</remarks>
public enum KeyTestStepKind
{
    /// <summary>Find the software that reaches the key (the desktop's PKCS#11 library).</summary>
    FindLibrary,

    /// <summary>List the readers (slots) the library sees.</summary>
    ListReaders,

    /// <summary>Connect to the key over the device's own transport (the phone's USB-C, NFC or Lightning).</summary>
    Connect,

    /// <summary>Select the key's PIV application — the first command a phone sends to the key.</summary>
    SelectPiv,

    /// <summary>Read the keys present and their certificates — public data, no PIN.</summary>
    ReadKey,

    /// <summary>Choose the key-management certificate the message is sealed to.</summary>
    ChooseCertificate,

    /// <summary>Seal a fresh random message to that certificate, on this device.</summary>
    Seal,

    /// <summary>Unlock the key with its PIN.</summary>
    Unlock,

    /// <summary>Open the message with the key.</summary>
    Open,

    /// <summary>Compare what was opened with what was sealed.</summary>
    Compare,
}

/// <summary>One step and its outcome.</summary>
/// <param name="Kind">Which step.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Finding">What it found or why it failed, in words.</param>
/// <param name="Key">The key it ran against (its label and serial), or null for a step about the machine.</param>
/// <param name="ErrorCode">The exact code where one failed: a PKCS#11 return value, an exception type and HRESULT.</param>
public sealed record KeyTestStep(
    KeyTestStepKind Kind,
    KeyTestOutcome Outcome,
    string Finding,
    string? Key = null,
    string? ErrorCode = null);

/// <summary>The app, the operating system and the path to the key.</summary>
/// <param name="App">The app's name, e.g. <c>SimplArchive Desktop</c>.</param>
/// <param name="AppVersion">Its version.</param>
/// <param name="OperatingSystem">The operating system and its version, as the runtime describes it.</param>
/// <param name="Architecture">The process architecture (<c>Arm64</c>, <c>X64</c>).</param>
/// <param name="Library">The PKCS#11 library found, or null when none was.</param>
/// <param name="Readers">The readers (slots) seen, with whether a key was in each.</param>
/// <param name="Connection">
/// How the key reached a device that needs no library (the phone: <c>USB-C</c>, <c>NFC</c>, <c>Lightning</c>), or null.
/// Set, the report names it instead of saying "no PKCS#11 library found", which would send support after a library a
/// phone never has.
/// </param>
public sealed record KeyTestEnvironment(
    string App,
    string AppVersion,
    string OperatingSystem,
    string Architecture,
    KeyTestLibrary? Library,
    IReadOnlyList<KeyTestReader> Readers,
    string? Connection = null)
{
    /// <summary>This machine's operating system and architecture, from the runtime.</summary>
    public static KeyTestEnvironment ThisMachine(
        string app, string appVersion, KeyTestLibrary? library, IReadOnlyList<KeyTestReader> readers, string? connection = null) =>
        new(app, appVersion, RuntimeInformation.OSDescription.Trim(), RuntimeInformation.ProcessArchitecture.ToString(),
            library, readers, connection);
}

/// <summary>The PKCS#11 library a desktop reached the key through.</summary>
/// <param name="Path">Where it was loaded from.</param>
/// <param name="Version">Its own version (CK_INFO.libraryVersion), when it could be read.</param>
/// <param name="Manufacturer">Its manufacturer as it names itself.</param>
/// <param name="Description">Its description as it names itself.</param>
/// <param name="InterfaceVersion">The PKCS#11 interface version it implements (CK_INFO.cryptokiVersion).</param>
public sealed record KeyTestLibrary(
    string Path,
    string? Version = null,
    string? Manufacturer = null,
    string? Description = null,
    string? InterfaceVersion = null);

/// <summary>A reader (a PKCS#11 slot) and whether a key was in it.</summary>
public sealed record KeyTestReader(string Description, bool KeyPresent);

/// <summary>A key as it describes itself, with the slots holding keys and its certificates.</summary>
/// <param name="Label">Its label, e.g. <c>YubiKey PIV #36771502</c>.</param>
/// <param name="SerialNumber">Its serial number — included on purpose; the window says so before copying.</param>
/// <param name="KeySlots">The key objects it shows (their labels), i.e. which slots hold a key.</param>
public sealed record KeyTestKey(
    string Label,
    string Manufacturer,
    string Model,
    string Firmware,
    string SerialNumber,
    IReadOnlyList<string> KeySlots,
    IReadOnlyList<KeyTestCertificate> Certificates);

/// <summary>One certificate on a key — public data, readable without the PIN.</summary>
/// <param name="Label">The object's label on the key (which slot it sits in).</param>
/// <param name="KeyType">The key's type and size: <c>RSA 2048</c>, <c>EC P-256</c>.</param>
/// <param name="ThumbprintSha256">SHA-256 of the certificate, lower-case hex — the form the archive enrols it by.</param>
/// <param name="Tested">Whether this is the certificate the test sealed to.</param>
public sealed record KeyTestCertificate(
    string Label,
    string Subject,
    string Issuer,
    string KeyType,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string ThumbprintSha256,
    bool Tested)
{
    /// <summary>Describes <paramref name="certificate"/> the way the report prints it.</summary>
    public static KeyTestCertificate From(string label, X509Certificate2 certificate, bool tested) => new(
        label,
        certificate.Subject,
        certificate.Issuer,
        KeyTypeOf(certificate),
        new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        ThumbprintOf(certificate),
        tested);

    /// <summary>SHA-256 of the certificate, lower-case hex, as the archive's reader-certificate set carries it.</summary>
    public static string ThumbprintOf(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

    /// <summary>The key's type and size, e.g. <c>RSA 2048</c> or <c>EC P-256</c>; the algorithm's OID when neither.</summary>
    public static string KeyTypeOf(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null)
            {
                return $"RSA {rsa.KeySize}";
            }

            using var dsa = certificate.GetECDsaPublicKey();
            if (dsa is not null)
            {
                return $"EC {CurveName(dsa.ExportParameters(false).Curve, dsa.KeySize)}";
            }

            using var dh = certificate.GetECDiffieHellmanPublicKey();
            if (dh is not null)
            {
                return $"EC {CurveName(dh.ExportParameters(false).Curve, dh.KeySize)}";
            }
        }
        catch (CryptographicException)
        {
            // A key the runtime cannot load (an X25519 certificate, say) is still named by its OID below.
        }

        var oid = certificate.PublicKey.Oid;
        return $"{oid.FriendlyName ?? "unknown"} ({oid.Value})";
    }

    private static string CurveName(ECCurve curve, int keySize) =>
        curve.Oid?.FriendlyName switch
        {
            "nistP256" or "ECDSA_P256" or "ECDH_P256" => "P-256",
            "nistP384" or "ECDSA_P384" or "ECDH_P384" => "P-384",
            "nistP521" or "ECDSA_P521" or "ECDH_P521" => "P-521",
            { Length: > 0 } named => named,
            _ => curve.Oid?.Value ?? $"{keySize} bit",
        };
}

/// <summary>
/// The archive's side of the key, read through rels the installation advertises — only when somebody is signed in.
/// </summary>
/// <remarks>
/// A null field means <b>the server does not advertise it</b>, which the report says in those words rather than
/// guessing — an installation older than the rel it is read through.
/// </remarks>
/// <param name="ServerAddress">The installation's root address (no credential rides in it).</param>
/// <param name="ServerVersion">The server's build version.</param>
/// <param name="CertificateSetSource">Where the user's reader-certificate set comes from (<c>module</c>, <c>own</c>, <c>none</c>).</param>
/// <param name="CertificateSetReason">Why the set is empty, when it is.</param>
/// <param name="Enrolments">For each tested certificate, whether it is in the user's set; null when not advertised.</param>
/// <param name="Encryption">The tenant's encryption mode and its four answers; null when not advertised.</param>
/// <param name="PinOncePerSession">Whether a phone may hold the key's PIN for a session; null when not advertised.</param>
/// <param name="OfflineUnlockMode">The offline cache's unlock mode (<c>Session</c>, <c>Document</c>, <c>UserChooses</c>).</param>
/// <param name="Problems">Reads that failed, in words — the archive side is best-effort and says what it missed.</param>
public sealed record KeyTestArchiveSide(
    string ServerAddress,
    string? ServerVersion,
    string? CertificateSetSource,
    string? CertificateSetReason,
    IReadOnlyList<KeyTestEnrolment>? Enrolments,
    KeyTestEncryption? Encryption,
    bool? PinOncePerSession,
    string? OfflineUnlockMode,
    IReadOnlyList<string> Problems);

/// <summary>
/// The tenant's encryption mode and its membership answers, as the server states them (core ADR 0834: the mode is not
/// an ordered scale, so the answers are what a reader acts on).
/// </summary>
public sealed record KeyTestEncryption(
    string Mode, bool WrapsAtRest, bool RefusesPlaintextDoors, bool DeliversEnvelopes, bool EnvelopesMail);

/// <summary>Whether one certificate is enrolled for the signed-in user.</summary>
public sealed record KeyTestEnrolment(string ThumbprintSha256, bool Enrolled);
