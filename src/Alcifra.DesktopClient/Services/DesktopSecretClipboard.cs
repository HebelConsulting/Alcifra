using Avalonia.Input;
using Avalonia.Input.Platform;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The desktop's clipboard as a secret field needs it (ADR 0968, the core's <see cref="ISecretClipboard"/>): the text,
/// plus the markers each platform's clipboard history and sync honour, so a copied password is kept out of them where the
/// platform knows the notion. The 30-second clearing is the core's <see cref="SecretCopy"/>, which asks this clipboard
/// whether it still holds our value before it clears it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What each platform is told.</b> Windows: <c>ExcludeClipboardContentFromMonitorProcessing</c>, and
/// <c>CanIncludeInClipboardHistory</c> / <c>CanUploadToCloudClipboard</c> set to zero, which keep the copy out of the
/// clipboard history and the cross-device clipboard. macOS: the <c>org.nspasteboard.ConcealedType</c> convention, which
/// clipboard managers read as "do not record". Linux: the <c>x-kde-passwordManagerHint</c> convention of the desktop
/// environments that keep a clipboard history. None of them is a guarantee: an application that reads the clipboard
/// while the secret is on it reads it, which is what the 30 seconds bound.
/// </para>
/// <para>
/// A platform that refuses the markers still gets the text: copying the password is what the user asked for, and the
/// clearing still applies.
/// </para>
/// </remarks>
internal sealed class DesktopSecretClipboard(Func<IClipboard?> clipboard) : ISecretClipboard
{
    private static readonly byte[] Zero = [0, 0, 0, 0];

    public async Task SetSensitiveAsync(string text)
    {
        if (clipboard() is not { } board)
        {
            return;
        }

        try
        {
            var item = new DataTransferItem();
            item.SetText(text);
            foreach (var (format, value) in Markers())
            {
                item.Set(DataFormat.CreateBytesPlatformFormat(format), value);
            }

            var transfer = new DataTransfer();
            transfer.Add(item);
            await board.SetDataAsync(transfer);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DesktopLog.Debug("The clipboard refused the sensitive markers; the text is copied without them: {Reason}", e.Message);
            await board.SetTextAsync(text);
        }
    }

    public async Task<string?> GetTextAsync() => clipboard() is { } board ? await board.TryGetTextAsync() : null;

    // The platform's "keep this out of the history" markers, by their system names.
    private static IEnumerable<(string Format, byte[] Value)> Markers() =>
        OperatingSystem.IsWindows()
            ? [("ExcludeClipboardContentFromMonitorProcessing", Zero), ("CanIncludeInClipboardHistory", Zero), ("CanUploadToCloudClipboard", Zero)]
            : OperatingSystem.IsMacOS()
                ? [("org.nspasteboard.ConcealedType", Zero)]
                : [("x-kde-passwordManagerHint", System.Text.Encoding.ASCII.GetBytes("secret"))];

    public async Task ClearAsync()
    {
        if (clipboard() is { } board)
        {
            await board.ClearAsync();
        }
    }
}
