using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Localization;

namespace Alcifra.ApiClient;

/// <summary>Which state a preview shows for a sealed document the client did not open (ADR 0969).</summary>
public enum SealedContentState
{
    /// <summary>A key of the user's that can open it is not at hand, or its prompt was cancelled: present it, ask again.</summary>
    Sealed,

    /// <summary>The envelope is addressed to none of the user's enrolled keys.</summary>
    SealedForOtherKeys,

    /// <summary>The key at hand is not enrolled for the signed-in user, so it is not used — no PIN is sent to it.</summary>
    KeyNotEnrolledForYou,

    /// <summary>Which keys are enrolled for the user is not known (never seen online), so no key is used.</summary>
    EnrolledKeysUnknown,
}

/// <summary>
/// What a preview shows for a sealed document the client did not open: a title, perhaps a subline, and always
/// "Open with my key" (ADR 0969).
/// </summary>
/// <param name="KeyLabel">The one enrolled key the document is sealed to, where exactly one is: "Insert your key ‹label›".</param>
public sealed record SealedOutcome(SealedContentState State, string? KeyLabel = null)
{
    /// <summary>No key of the user's at hand, and nothing more known about which one it would be.</summary>
    public static readonly SealedOutcome NoKey = new(SealedContentState.Sealed);

    /// <summary>The key at hand is not enrolled for the signed-in user.</summary>
    public static readonly SealedOutcome NotEnrolled = new(SealedContentState.KeyNotEnrolledForYou);

    /// <summary>The user's enrolled keys are not known.</summary>
    public static readonly SealedOutcome Unknown = new(SealedContentState.EnrolledKeysUnknown);

    /// <summary>The title's localisation key.</summary>
    public string TitleKey => (State, KeyLabel) switch
    {
        (SealedContentState.SealedForOtherKeys, _) => "PvSealedForOtherKeys",
        (SealedContentState.KeyNotEnrolledForYou, _) => "PvKeyNotEnrolledForYou",
        (SealedContentState.EnrolledKeysUnknown, _) => "PvEnrolledKeysUnknown",
        (_, { Length: > 0 }) => "PvSealedInsertKey",
        _ => "PvSealedNoKey",
    };

    /// <summary>The subline's localisation key, or null for none.</summary>
    public string? SublineKey => State switch
    {
        SealedContentState.SealedForOtherKeys => "PvNoneEnrolledAmongThem",
        SealedContentState.KeyNotEnrolledForYou => "PvKeyNotEnrolledForYouHint",
        SealedContentState.EnrolledKeysUnknown => "PvEnrolledKeysUnknownHint",
        _ => null,
    };

    /// <summary>The title in the current UI culture.</summary>
    public string Title => TitleKey == "PvSealedInsertKey"
        ? string.Format(CultureInfo.CurrentCulture, Strings.Get(TitleKey), KeyLabel)
        : Strings.Get(TitleKey);

    /// <summary>The subline in the current UI culture, or null.</summary>
    public string? Subline => SublineKey is { } key ? Strings.Get(key) : null;

    /// <summary>Title and subline as one text, a line apart — for a surface with a single line to say it in.</summary>
    public string Text => Subline is { } subline ? $"{Title}\n{subline}" : Title;
}

/// <summary>
/// The rules the desktop and the phone apply to a sealed envelope (ADR 0969): which keys they may use at all, and which
/// plain state a preview shows when none opened it, rather than a spinner and an error in the status line.
/// </summary>
/// <remarks>
/// <para>
/// Shared, in the client library both native apps use, so the two answer the same document identically (ADR 0917,
/// "moved, not copied"): only how each host REACHES a key differs — the desktop's certificate store and card reader,
/// the phone's key over NFC or USB — and that is an input here, never the rule.
/// </para>
/// <para>
/// <b>A key is used only if it is enrolled for the signed-in user</b> (owner, 2026-10-24): an administrator who may
/// download another user's client-sealed items must not open them with that user's token in a reader. The check is made
/// from public data — the certificate on the token against the user's enrolled set — before any PIN reaches the key.
/// </para>
/// <para>
/// <b>Judged against the user's WHOLE enrolled set</b>: an envelope addressed to an enrolled key that is not at hand is
/// "sealed — insert your key", naming it where exactly one is; only an envelope addressed to none of them is "sealed for
/// other keys".
/// </para>
/// </remarks>
public static class SealedContent
{
    /// <summary>Whether <paramref name="certificate"/>'s key may be used: it is one of the user's enrolled keys.</summary>
    public static bool Admits(X509Certificate2 certificate, EnrolledKeysAnswer enrolled) =>
        enrolled.Keys is { } keys && keys.Any(k => k.Names(certificate));

    /// <summary>
    /// The state for an envelope no admitted key opened.
    /// </summary>
    /// <param name="recipients">The envelope's recipients, read from public data (<see cref="EnvelopeRecipients.Of(byte[])"/>).</param>
    /// <param name="present">The certificates of every key the host could reach: a software key, the cards in a reader.</param>
    /// <param name="enrolled">The signed-in user's enrolled keys.</param>
    /// <remarks>
    /// In this order: unknown enrolled keys refuse everything; a present addressee that is not the user's is named as
    /// such (it was refused, not missing); otherwise the user's enrolled set decides between "insert your key" and
    /// "sealed for other keys".
    /// </remarks>
    public static SealedOutcome Judge(
        IReadOnlyList<(string Issuer, string Serial)> recipients, IEnumerable<X509Certificate2> present, EnrolledKeysAnswer enrolled)
    {
        if (enrolled.Keys is not { } keys)
        {
            return SealedOutcome.Unknown;
        }

        var addressedHere = present.Where(c => EnvelopeRecipients.Matches(c, recipients)).ToList();
        if (addressedHere.Count > 0 && !addressedHere.Any(c => Admits(c, enrolled)))
        {
            return SealedOutcome.NotEnrolled;
        }

        return ByEnrolledKeys(recipients, keys);
    }

    /// <summary>The judgement against the user's whole enrolled set alone.</summary>
    public static SealedOutcome ByEnrolledKeys(
        IReadOnlyList<(string Issuer, string Serial)> recipients, IReadOnlyList<EnrolledKey> enrolled)
    {
        var addressed = enrolled.Where(k => EnvelopeRecipients.Matches(k.Issuer, k.Serial, recipients)).ToList();
        if (addressed.Count == 0)
        {
            return new SealedOutcome(SealedContentState.SealedForOtherKeys);
        }

        var labels = addressed.Select(k => k.Label).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new SealedOutcome(SealedContentState.Sealed, labels.Count == 1 && addressed.Count == 1 ? labels[0] : null);
    }

    /// <summary>
    /// The state a failed read of sealed content stands for, or null when it is a real failure, to report as one.
    /// </summary>
    /// <remarks>
    /// A cancelled key prompt is an <see cref="OperationCanceledException"/> (the hosts' convention, ADR 0967) and is told
    /// from a timed-out request — a <see cref="TaskCanceledException"/>, which is a network failure — by type.
    /// </remarks>
    public static SealedOutcome? OutcomeOf(Exception failure) => failure switch
    {
        ISealedRefusal refusal => refusal.Outcome,
        TaskCanceledException => null,
        OperationCanceledException => SealedOutcome.NoKey,
        _ => null,
    };
}
