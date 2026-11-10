using System.Security.Cryptography.X509Certificates;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Inspection;
using Alcifra.HardwareKeys;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// <i>Which of my keys opens it?</i> for one sealed file (#2291): the shared key try (<see cref="KeyTry"/>, the CLI's
/// <c>--try-open</c>) over what this computer can reach, in the order the content funnel uses (ADR 0832) — the keys in the
/// certificate store first, then the card. Only the signed-in user's own enrolled keys are touched (ADR 0969); the content is
/// wiped at once and never kept.
/// </summary>
/// <param name="sealedBytes">
/// The file as stored, read on the first try rather than when the dialog opens: a document's envelope is fetched only if a
/// key is actually tried.
/// </param>
public sealed class SealKeyTrier(Func<CancellationToken, Task<byte[]>> sealedBytes)
{
    private (byte[] Cms, IReadOnlyList<InspectedRecipient> Recipients)? _envelope;

    /// <summary>A trier for a local file's bytes.</summary>
    public static SealKeyTrier Of(byte[] bytes) => new(_ => Task.FromResult(bytes));

    /// <summary>The keys this computer holds and the card, the first that opens it winning.</summary>
    public async Task<KeyTryResult> TryMineAsync(CancellationToken cancellationToken = default)
    {
        if (await EnvelopeAsync(cancellationToken) is not var (cms, recipients))
        {
            return new KeyTryResult(KeyTryOutcome.NoCard, null);
        }

        var enrolled = await EnvelopeOpener.SignedIn(cancellationToken);
        bool Admitted(X509Certificate2 certificate) => SealedContent.Admits(certificate, enrolled);

        KeyTryResult? refused = null;
        foreach (var key in EnvelopeOpener.Keys().Cast<X509Certificate2>().Where(k => recipients.Any(r => r.Reference.Is(k)) && Admitted(k)))
        {
            var tried = KeyTry.WithKey(cms, recipients, key, message => DesktopLog.Debug("{Reason}", message));
            if (tried.Opened)
            {
                return tried;
            }

            refused ??= tried;
        }

        var card = await KeyTry.WithCardAsync(cms, recipients, Admitted);
        return card.Opened || refused is null ? card : refused;
    }

    /// <summary>A key from a file the user picked (a PKCS#12 file, or a PEM with its private key).</summary>
    public async Task<KeyTryResult> TryKeyAsync(X509Certificate2 key, CancellationToken cancellationToken = default) =>
        await EnvelopeAsync(cancellationToken) is var (cms, recipients)
            ? KeyTry.WithKey(cms, recipients, key, message => DesktopLog.Debug("{Reason}", message))
            : new KeyTryResult(KeyTryOutcome.NotAddressedToKey, key.Subject);

    // The envelope inside the stored bytes (bare, in an S/MIME message, or an archive's key envelope), read once.
    private async Task<(byte[] Cms, IReadOnlyList<InspectedRecipient> Recipients)?> EnvelopeAsync(CancellationToken cancellationToken) =>
        _envelope ??= SealedInspector.Inspect(await sealedBytes(cancellationToken)) is { Kind: SealedKind.Enveloped or SealedKind.Archive, Cms: { } cms } inspection
            ? (cms, inspection.Recipients)
            : null;
}
