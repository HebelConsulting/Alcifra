using Alcifra.Presentation;

namespace Alcifra.UnitTests.PasswordStore;

// The secret field both native clients draw (owner decisions 2026-10-23, ADR 0964): hidden by default, shown only while
// held, copied as sensitive and cleared after 30 s if the clipboard still holds our value. The clock is a seam.
public sealed class SecretFieldTests
{
    [Fact]
    public void The_password_and_marked_fields_are_secret_and_the_rest_plain_and_all_copyable()
    {
        Assert.Equal(new SecretFieldPresentation(true, true), SecretFields.For(isPassword: true, markedSecret: false));
        Assert.Equal(new SecretFieldPresentation(true, true), SecretFields.For(isPassword: false, markedSecret: true));
        Assert.Equal(new SecretFieldPresentation(false, true), SecretFields.For(isPassword: false, markedSecret: false));
    }

    [Fact]
    public void A_secret_is_hidden_until_pressed_and_hidden_again_on_release()
    {
        var reveal = new SecretReveal();
        var changes = 0;
        reveal.Changed += () => changes++;

        Assert.Equal(SecretFields.Mask, reveal.Display("hunter2"));
        reveal.Press();
        Assert.Equal("hunter2", reveal.Display("hunter2"));
        reveal.Release();
        Assert.Equal(SecretFields.Mask, reveal.Display("hunter2"));
        reveal.Release();
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_copied_secret_is_marked_sensitive_and_cleared_after_thirty_seconds()
    {
        var clipboard = new FakeClipboard();
        var time = new ManualTime();
        var copy = new SecretCopy(clipboard, time).CopyAsync("hunter2");

        Assert.Equal("hunter2", clipboard.Text);
        Assert.True(clipboard.Sensitive);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(copy.IsCompleted);
        Assert.Equal("hunter2", clipboard.Text);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await copy);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task Something_copied_since_is_left_alone()
    {
        var clipboard = new FakeClipboard();
        var time = new ManualTime();
        var copy = new SecretCopy(clipboard, time).CopyAsync("hunter2");
        clipboard.Text = "the user's own text";

        time.Advance(SecretFields.ClipboardLifetime);

        Assert.False(await copy);
        Assert.Equal("the user's own text", clipboard.Text);
    }
}
