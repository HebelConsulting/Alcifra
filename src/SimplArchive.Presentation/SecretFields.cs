namespace SimplArchive.Presentation;

/// <summary>How one field of an opened password entry is shown (ADR 0964): hidden like a password, and copyable.</summary>
/// <param name="IsSecret">Hidden by default, revealed only while held, copied as sensitive and cleared after 30 s.</param>
/// <param name="CanCopy">Offers a copy button.</param>
public sealed record SecretFieldPresentation(bool IsSecret, bool CanCopy);

/// <summary>
/// The SECRET-FIELD rules both native clients answer identically (owner decisions 2026-10-23, ADR 0964): which fields are
/// secret, and how a secret is shown and copied. Each client draws its own control; the behaviour lives here once.
/// </summary>
public static class SecretFields
{
    /// <summary>How long a copied secret may stay on the clipboard.</summary>
    public static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(30);

    /// <summary>What a hidden secret shows instead of itself: a fixed width, so its length is not given away.</summary>
    public const string Mask = "••••••••";

    /// <summary>The password is secret; so is any field its author marked secret; every other field (user name, URL, the
    /// rest) is shown plainly. All are copyable.</summary>
    public static SecretFieldPresentation For(bool isPassword, bool markedSecret) => new(isPassword || markedSecret, CanCopy: true);
}

/// <summary>
/// The eye of one secret field: hidden by default, shown ONLY WHILE HELD. Pure state, so a client binds its press and
/// release and a test drives it without a screen.
/// </summary>
public sealed class SecretReveal
{
    /// <summary>Whether the value is being shown: only between a press and its release.</summary>
    public bool IsRevealed { get; private set; }

    /// <summary>Raised when <see cref="IsRevealed"/> changes, for a client that redraws.</summary>
    public event Action? Changed;

    public void Press() => Set(true);

    /// <summary>The finger lifted, the pointer left, the window lost focus: every one of them hides it again.</summary>
    public void Release() => Set(false);

    /// <summary>What to draw for <paramref name="value"/> now.</summary>
    public string Display(string value) => IsRevealed ? value : SecretFields.Mask;

    private void Set(bool revealed)
    {
        if (IsRevealed != revealed)
        {
            IsRevealed = revealed;
            Changed?.Invoke();
        }
    }
}

/// <summary>The platform clipboard as the secret field needs it: each client implements it with its own API.</summary>
public interface ISecretClipboard
{
    /// <summary>Puts text on the clipboard, marked sensitive where the platform knows the notion (kept out of history,
    /// previews and cloud sync).</summary>
    Task SetSensitiveAsync(string text);

    /// <summary>What the clipboard holds now, or null when it holds no text (or the platform will not say).</summary>
    Task<string?> GetTextAsync();

    Task ClearAsync();
}

/// <summary>
/// Copying a secret (ADR 0964): onto the clipboard marked sensitive, and cleared after
/// <see cref="SecretFields.ClipboardLifetime"/> IF it still holds our value, so something the user copied since is never
/// wiped. The clock is a <see cref="TimeProvider"/>, so a test moves time instead of waiting.
/// </summary>
public sealed class SecretCopy(ISecretClipboard clipboard, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Copies <paramref name="secret"/>; the returned task ends when the clipboard was cleared or left alone.</summary>
    /// <returns>True when it was cleared, false when the clipboard had changed meanwhile and was left as it was.</returns>
    public async Task<bool> CopyAsync(string secret, CancellationToken cancellationToken = default)
    {
        await clipboard.SetSensitiveAsync(secret);
        await Task.Delay(SecretFields.ClipboardLifetime, _time, cancellationToken);
        if (!string.Equals(await clipboard.GetTextAsync(), secret, StringComparison.Ordinal))
        {
            return false;
        }

        await clipboard.ClearAsync();
        return true;
    }
}
