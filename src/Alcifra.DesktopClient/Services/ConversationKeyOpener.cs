using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using Alcifra.Messenger.Client;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Opens the Messenger's sealed keys and files with a key this machine can reach (core #1813): a bare CMS envelope
/// addressed to the reader's certificates, which the server never wraps in MIME. The same keys, in the same order, as
/// a sealed document (ADR 0832): a software key first — no PIN, no hardware — then the card, which may ask for the PIN. A cancelled PIN prompt is
/// an <see cref="OperationCanceledException"/>, as the Messenger's screens expect of a host's opener (ADR 0967).
/// </summary>
internal sealed class ConversationKeyOpener : IConversationKeyOpener
{
    public async Task<byte[]> OpenAsync(byte[] wrappedKey, CancellationToken cancellationToken = default)
    {
        var recipients = EnvelopeRecipients.Of(wrappedKey);

        // Only the signed-in user's own keys, software or card, as for a sealed document (ADR 0969).
        var enrolled = await EnvelopeOpener.SignedIn(cancellationToken);
        if (enrolled.Keys is null)
        {
            throw new EnvelopeStaysSealedException("the keys enrolled for you could not be checked", SealedOutcome.Unknown);
        }

        bool Admitted(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => SealedContent.Admits(certificate, enrolled);

        if (OpenWithSoftwareKey(wrappedKey, recipients, Admitted) is { } opened)
        {
            return opened;
        }

        if (await CardEnvelopeOpener.TryOpenRawAsync(wrappedKey, recipients, Admitted) is { } fromCard)
        {
            return fromCard;
        }

        // A cancelled PIN prompt is the user's decision, not a failure (ADR 0967): an OperationCanceledException, after
        // which the thread shows its messages sealed and asks again only when the user does (the Messenger's KeyPrompt).
        throw EnvelopeOpener.Refusal(recipients, enrolled);
    }

    // A key in the user's certificate store that the envelope is addressed to and that is enrolled for the signed-in user;
    // null when none is, or it will not open.
    private static byte[]? OpenWithSoftwareKey(
        byte[] cms, IReadOnlyList<(string Issuer, string Serial)> recipients, Func<System.Security.Cryptography.X509Certificates.X509Certificate2, bool> admitted)
    {
        var all = EnvelopeOpener.Keys();
        var keys = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection(
            all.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>().Where(admitted).ToArray());
        if (!keys.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>().Any(k => EnvelopeRecipients.Matches(k, recipients)))
        {
            return null;
        }

        try
        {
            var envelope = new EnvelopedCms();
            envelope.Decode(cms);
            envelope.Decrypt(keys);
            return envelope.ContentInfo.Content;
        }
        catch (CryptographicException)
        {
            return null;   // addressed here but not openable with this store: the card may still hold the key
        }
    }
}
