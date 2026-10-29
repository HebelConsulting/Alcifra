using System.Globalization;
using Alcifra.Localization;

namespace Alcifra.ApiClient.KeyTest;

/// <summary>
/// The verdict a client shows above the report, in the app's language: one headline and one short line saying why
/// (owner 2026-10-22, ADR 0962). One implementation, so the desktop and the phone say the same thing for one key.
/// </summary>
/// <param name="Verdict">The report's verdict, which decides the colour and the icon.</param>
/// <param name="Headline">"Your hardware key works with Alcifra" or "… does not work …".</param>
/// <param name="Reason">
/// The failing step's reason in plain words; for a key that works, whether its certificate is enrolled (signed in);
/// null when there is nothing to add.
/// </param>
public sealed record KeyTestVerdictLine(KeyTestVerdict Verdict, string Headline, string? Reason);

/// <summary>Turns a <see cref="KeyTestReport"/> into its <see cref="KeyTestVerdictLine"/>.</summary>
/// <remarks>
/// <para>
/// <b>Read from the step kind and its exact code</b>, both of which are a stable contract support reads a report by
/// (<see cref="KeyTestStepKind"/>): the desktop's PKCS#11 return values (<c>CKR_PIN_INCORRECT</c>,
/// <c>CKR_PIN_LOCKED</c>, <c>KEY_NOT_IN_READER</c>) and the phone's PIV status words (<c>SW 63Cn</c>, <c>SW 6983</c>).
/// The step's <see cref="KeyTestStep.Finding"/> is English for support and is never shown here.
/// </para>
/// <para>
/// <b>Tries left only where the key says.</b> A PIV key answers a wrong PIN with the count (<c>63Cn</c>); PKCS#11 has
/// no count, so the desktop says "wrong PIN" and warns that a few wrong entries block it.
/// </para>
/// </remarks>
public static class KeyTestVerdictText
{
    /// <summary>The headline and the reason for <paramref name="report"/>.</summary>
    public static KeyTestVerdictLine Of(KeyTestReport report) => report.Verdict switch
    {
        KeyTestVerdict.Works => new(KeyTestVerdict.Works, Strings.Get("KtWorks"), Enrolment(report)),
        KeyTestVerdict.Failed => new(KeyTestVerdict.Failed, Strings.Get("KtDoesNotWork"), ReasonOf(report.FirstFailure!)),
        _ => new(KeyTestVerdict.NotCompleted, Strings.Get("KtNotCompleted"), null),
    };

    /// <summary>Why <paramref name="failed"/> failed, in plain words.</summary>
    public static string ReasonOf(KeyTestStep failed) => failed.Kind switch
    {
        KeyTestStepKind.FindLibrary => Strings.Get("KtWhyNoLibrary"),
        KeyTestStepKind.Connect or KeyTestStepKind.ListReaders or KeyTestStepKind.ReadKey => Strings.Get("KtWhyNoKey"),
        KeyTestStepKind.SelectPiv => Strings.Get("KtWhyNoPiv"),
        KeyTestStepKind.ChooseCertificate => Strings.Get("KtWhyNoCertificate"),
        KeyTestStepKind.Seal => Strings.Get("KtWhyCannotSeal"),
        KeyTestStepKind.Unlock => UnlockReason(failed.ErrorCode),
        _ => Strings.Get("KtWhyNotOpened"),
    };

    /// <summary>
    /// The tries a PIV key says are left after a wrong PIN (<c>SW 63Cn</c>), or null when the code does not say —
    /// PKCS#11's <c>CKR_PIN_INCORRECT</c> carries no count.
    /// </summary>
    public static int? PinTriesLeft(string? code) =>
        code is { Length: >= 8 } && code.StartsWith("SW 63C", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(6, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var left)
            ? left
            : null;

    private static string UnlockReason(string? code) => (code, PinTriesLeft(code)) switch
    {
        (_, 0) => Strings.Get("KtWhyPinBlocked"),
        (_, { } left) => string.Format(CultureInfo.CurrentCulture, Strings.Get("KtWhyWrongPinLeft"), left),
        ({ } c, _) when c.StartsWith("CKR_PIN_INCORRECT", StringComparison.Ordinal) => Strings.Get("KtWhyWrongPin"),
        ({ } c, _) when c.StartsWith("CKR_PIN_LOCKED", StringComparison.Ordinal) || c.StartsWith("SW 6983", StringComparison.Ordinal)
            => Strings.Get("KtWhyPinBlocked"),
        ("KEY_NOT_IN_READER", _) => Strings.Get("KtWhyNoKey"),
        _ => Strings.Get("KtWhyNotUnlocked"),
    };

    // A key that works but is not enrolled opens nothing the archive delivers, so the verdict says so (Mobile ADR 0021).
    private static string? Enrolment(KeyTestReport report) => report.Archive?.Enrolments switch
    {
        null or [] => null,
        var enrolments when enrolments.All(e => e.Enrolled) => Strings.Get("MobKtEnrolled"),
        _ => Strings.Get("MobKtNotEnrolled"),
    };
}
