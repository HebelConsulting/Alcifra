using Alcifra.Presentation;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>
/// One field of an opened entry as a control binds it (Mobile ADR 0037; both native clients since ADR 0968), over the
/// shared rules (<see cref="SecretReveal"/>, <see cref="SecretCopy"/>, owner decisions 2026-10-23): a secret shows its
/// mask until the eye is HELD and again when it is let go; copying a secret marks the clipboard sensitive and clears it
/// after 30 s if it still holds this value. A plain field (user name, URL) shows its value and copies it as plain text.
/// </summary>
public sealed class SecretField(PasswordEntryRow row, ISecretClipboard clipboard, TimeProvider? time = null)
{
    private readonly SecretCopy _copy = new(clipboard, time);

    public PasswordEntryRow Row => row;

    /// <summary>The eye: pressed and released by the control; a secret is shown only in between.</summary>
    public SecretReveal Reveal { get; } = new();

    /// <summary>What the control draws now.</summary>
    public string Display => row.IsSecret ? Reveal.Display(row.Value) : row.Value;

    /// <summary>
    /// Copies the value. A secret's task ends when the clipboard was cleared (true) or left alone because it changed
    /// meanwhile (false); a plain field's at once, never cleared.
    /// </summary>
    public Task<bool> CopyAsync(Func<string, Task> copyPlain, CancellationToken cancellationToken = default) =>
        row.IsSecret ? _copy.CopyAsync(row.Value, cancellationToken) : CopyPlainAsync(copyPlain);

    /// <summary>The view went away, lost the screen, or the pointer slid off: nothing stays revealed.</summary>
    public void Hide() => Reveal.Release();

    private async Task<bool> CopyPlainAsync(Func<string, Task> copyPlain)
    {
        await copyPlain(row.Value);
        return false;
    }
}
