using Alcifra.Pass.Infrastructure;

namespace Alcifra.UnitTests;

// apass password --clip and apass otp --clip (owner, 2026-11-01): the secret goes onto the clipboard through the
// platform's own tool, as pass does it, and off it again after 45 s — but only if it is still there. These pin the tools
// and that rule; the real clipboard is not touched here, since it is the developer's own.
public class PassClipboardTests
{
    [Theory]
    [InlineData("MacOs", "pbcopy", "pbpaste")]
    [InlineData("Wayland", "wl-copy", "wl-paste")]
    [InlineData("X11", "xclip", "xclip")]
    [InlineData("Windows", "powershell", "powershell")]
    public void Each_platform_uses_its_own_clipboard_tool(string platform, string copy, string paste)
    {
        var tools = Clipboard.ToolsFor(Enum.Parse<Clipboard.Platform>(platform));

        Assert.Equal((copy, paste), (tools.Copy.FileName, tools.Paste.FileName));
    }

    [Fact]
    public void The_clipboard_is_cleared_only_if_it_still_holds_the_secret()
    {
        var hash = Clipboard.Hash("s3cret");

        Assert.True(Clipboard.ShouldClear("s3cret", hash));
        Assert.True(Clipboard.ShouldClear("s3cret\r\n", hash));     // the line ending PowerShell adds when it prints it
        Assert.False(Clipboard.ShouldClear("something copied since", hash));
        Assert.False(Clipboard.ShouldClear(null, hash));            // the clipboard could not be read: leave it alone
    }

    [Fact]
    public void Only_a_hash_reaches_the_clearing_process_never_the_secret()
    {
        var hash = Clipboard.Hash("s3cret");

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("s3cret", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clearing_waits_as_long_as_pass_does()
    {
        Assert.Equal(45, Clipboard.ClearAfterSeconds);
    }
}
