namespace Alcifra.HardwareKeys;

/// <summary>
/// The card no longer counts the session as logged in, although this process logged it in (#2041).
/// </summary>
/// <remarks>
/// The PIN state of a PIV card belongs to the CARD, not to a process: another program that merely loads the card's
/// PKCS#11 module (a slot listing is enough, measured 2026-10-31) clears it. The held session then looks alive, shows
/// no private key and refuses to decrypt. That is not "this card is not the addressee", and reporting it as such sent
/// the user looking for the wrong key while every retry used the same dead session.
/// </remarks>
public sealed class CardLoginLostException(string reason, Exception? inner = null)
    : Exception($"The card's login was reset: {reason}", inner);
