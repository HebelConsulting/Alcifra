using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;

namespace Alcifra.HardwareKeys;

/// <summary>
/// One card operation on a logged-in session, recovering ONCE when another program reset the card's login (#2041).
/// </summary>
/// <remarks>
/// <para>
/// <b>A held session is treated as no session</b> (owner, 2026-10-31): the dead one is dropped and the session is
/// opened again, which asks for the PIN exactly as on first use, and the operation runs once more. Only a HELD session
/// is retried. On one opened a moment ago the PIN was just accepted, so a missing key there means this key is not the
/// one, and asking again would be a second prompt whose answer cannot change anything.
/// </para>
/// <para>
/// Generic over the session so the rule is testable with a fake card; the card itself is reached only through the
/// three functions passed in.
/// </para>
/// </remarks>
internal static class CardLoginRecovery
{
    /// <summary>
    /// Runs <paramref name="work"/> on the session <paramref name="open"/> answers, reopening once after a lost login on a
    /// held session. Null when no session could be had (declined, blocked, gone) or the card is not the one.
    /// </summary>
    /// <param name="wasHeld">Whether the first <paramref name="open"/> returns a session held from before.</param>
    /// <param name="open">Opens the logged-in session: the held one, or a new one after asking for the PIN.</param>
    /// <param name="work">The card operation, throwing <see cref="CardLoginLostException"/> when the login is gone.</param>
    /// <param name="dropHeld">Drops the held session of this card, so the next <paramref name="open"/> asks for the PIN.</param>
    /// <param name="tokenSerial">The card, for the log.</param>
    public static async Task<TResult?> RunAsync<TSession, TResult>(
        bool wasHeld,
        Func<Task<TSession?>> open,
        Func<TSession, TResult?> work,
        Action dropHeld,
        string tokenSerial)
        where TSession : class
        where TResult : class
    {
        if (await open() is not { } session)
        {
            return null;
        }

        try
        {
            return work(session);
        }
        catch (CardLoginLostException e) when (wasHeld)
        {
            ApiClientSettings.Logger.LogInformation(
                "The login on card {Serial} was reset, probably by another program using the card; asking for the PIN again. {Reason}",
                tokenSerial, e.Message);
            dropHeld();
        }
        catch (CardLoginLostException e)
        {
            ApiClientSettings.Logger.LogDebug("Card {Serial} shows no usable key right after its login: {Reason}", tokenSerial, e.Message);
            return null;
        }

        if (await open() is not { } reopened)
        {
            return null;
        }

        try
        {
            return work(reopened);
        }
        catch (CardLoginLostException e)
        {
            ApiClientSettings.Logger.LogDebug("Card {Serial} lost its login again right after a new one: {Reason}", tokenSerial, e.Message);
            return null;
        }
    }
}
