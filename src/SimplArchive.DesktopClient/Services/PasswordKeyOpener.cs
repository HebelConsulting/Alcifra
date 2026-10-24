using SimplArchive.ApiClient;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens a password entry with the reader's HARDWARE key (ADR 0968): the card, through the same PKCS#11 session a sealed
/// document and the Messenger use, PIN asked once per card session. Deliberately NOT the funnel's opener chain, whose
/// first link is the certificate store: a password store's entries are sealed only to ATTESTED hardware certificates
/// (ADR 0963), and a path that tries a software key first is a path that would read one the day a software identity
/// matched. So this opener has no software link, and adding one is a policy change, not a convenience.
/// </summary>
/// <remarks>
/// A cancelled PIN prompt is an <see cref="OperationCanceledException"/>, which the shared wording says by nothing (the
/// user chose it); every other decline is <see cref="EnvelopeNotOpenedException"/> naming what is missing (no reader, no
/// card, not addressed to it). The card session remembers a declined prompt, so a re-seal asks once and an entry this key
/// cannot open is reported rather than asked about again.
/// </remarks>
internal sealed class PasswordKeyOpener : IEnvelopeOpener
{
    public static readonly PasswordKeyOpener Instance = new();

    /// <summary>The openers tried, in order: the card alone. Replaced in tests by a key held in memory.</summary>
    internal static IReadOnlyList<EnvelopeOpener.Opener> Openers { get; set; } = [CardEnvelopeOpener.TryOpen];

    /// <summary>Whether the user's last PIN prompt was cancelled; replaced in tests with the card's.</summary>
    internal static Func<bool> Declined { get; set; } = () => CardSession.HasDeclined;

    /// <summary>The user asked to open an entry: a PIN prompt they cancelled before is asked again.</summary>
    public static void UserAsked() => CardModule.ForgetDeclines();

    public async Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
    {
        var enveloped = SmimeEnvelope.Unwrap(envelope);
        var recipients = EnvelopeRecipients.Of(enveloped);
        foreach (var opener in Openers)
        {
            if (await opener(enveloped, recipients) is { } opened)
            {
                return opened;
            }
        }

        if (Declined())
        {
            throw new OperationCanceledException("The key prompt was cancelled.");
        }

        throw new EnvelopeNotOpenedException(CardEnvelopeOpener.WhatIsMissing());
    }
}
