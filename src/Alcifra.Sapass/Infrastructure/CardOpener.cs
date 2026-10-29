using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.HardwareKeys;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The key path: opens a CMS envelope with the card in the reader, through the SAME implementation the desktop
/// uses (<see cref="CardEnvelopeOpener"/>) — the content funnel on both hosts decides by the type it was served.
/// </summary>
internal sealed class CardOpener : IEnvelopeOpener
{
    public async Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
    {
        if (!Envelopes.IsEnvelope(contentType))
        {
            return (envelope, contentType);
        }

        var enveloped = SmimeEnvelope.Unwrap(envelope);

        // Asked from public data before any PIN: a card is only unlocked for an envelope it can actually open.
        var recipients = EnvelopeRecipients.Of(enveloped);

        // Admitted: every certificate on the user's card. The store's envelopes are sealed by the server only to
        // ATTESTED, enrolled certificates (ADR 0964), and this tool has no session-wide enrolled-key answer of its
        // own — the check that matters is that the envelope names this card, which the opener makes first.
        return await CardEnvelopeOpener.TryOpen(enveloped, recipients, _ => true)
            ?? throw new EnvelopeNotOpenedException("no card in a reader holds a key this entry is sealed to");
    }
}

/// <summary>
/// The PIN, typed at the terminal with no echo — the tool's half of "the PIN is asked once and never kept" (ADR 0832).
/// </summary>
internal static class PinPrompt
{
    /// <summary>Asks for the card's PIN; null when there is no terminal to ask at (a pipe is not a person).</summary>
    public static Task<string?> AskAsync(CardBeingUnlocked card) =>
        Task.FromResult(SecretInput.ReadHidden($"PIN for {card.TokenLabel} ({card.ObjectLabel}):"));
}
