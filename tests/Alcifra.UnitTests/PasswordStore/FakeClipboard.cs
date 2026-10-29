using Alcifra.Presentation;

namespace Alcifra.UnitTests.PasswordStore;

// The platform clipboard as the secret field sees it, in memory: what it holds and whether it was marked sensitive.
internal sealed class FakeClipboard : ISecretClipboard
{
    public string? Text { get; set; }

    public bool Sensitive { get; private set; }

    public Task SetSensitiveAsync(string text)
    {
        (Text, Sensitive) = (text, true);
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);

    public Task ClearAsync()
    {
        Text = null;
        return Task.CompletedTask;
    }
}
