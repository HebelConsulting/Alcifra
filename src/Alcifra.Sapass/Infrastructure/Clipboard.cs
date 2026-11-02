using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The system clipboard, through the platform's own tool as pass does it, and the clearing that follows (owner,
/// 2026-11-01): a secret copied with <c>--clip</c> is cleared after <see cref="ClearAfterSeconds"/>, or sooner where a
/// verification code expires first, and only if the clipboard still holds it — anything copied meanwhile is left alone.
/// </summary>
/// <remarks>
/// The clearing runs in a detached <c>apass</c> (<see cref="ClearVerb"/>) so the command returns at once. The secret
/// never travels to it: only its SHA-256 goes over that process's stdin, never its command line, which other users of
/// the machine can read.
/// </remarks>
internal static class Clipboard
{
    /// <summary>pass's own default (<c>PASSWORD_STORE_CLIP_TIME</c>).</summary>
    public const int ClearAfterSeconds = 45;

    /// <summary>The hidden verb the clearing runs as.</summary>
    public const string ClearVerb = "__clip-clear";

    public enum Platform
    {
        MacOs,
        Windows,
        Wayland,
        X11,
    }

    /// <summary>A program and its arguments.</summary>
    public sealed record Tool(string FileName, IReadOnlyList<string> Arguments);

    public static Platform Current =>
        OperatingSystem.IsMacOS() ? Platform.MacOs
        : OperatingSystem.IsWindows() ? Platform.Windows
        : Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? Platform.Wayland
        : Platform.X11;

    /// <summary>How text is put on, read off and cleared from the clipboard on <paramref name="platform"/>; the copy and
    /// the clear take their text on stdin.</summary>
    public static (Tool Copy, Tool Paste, Tool Clear) ToolsFor(Platform platform) => platform switch
    {
        Platform.MacOs => (new("pbcopy", []), new("pbpaste", []), new("pbcopy", [])),
        Platform.Windows => (
            new("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Set-Clipboard -Value ([Console]::In.ReadToEnd())"]),
            new("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard -Raw"]),
            new("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Set-Clipboard -Value $null"])),
        Platform.Wayland => (new("wl-copy", []), new("wl-paste", ["--no-newline"]), new("wl-copy", ["--clear"])),
        _ => (new("xclip", ["-selection", "clipboard"]), new("xclip", ["-selection", "clipboard", "-o"]), new("xclip", ["-selection", "clipboard"])),
    };

    /// <summary>Copies <paramref name="text"/> and starts the clearing after <paramref name="seconds"/>.</summary>
    public static async Task CopyAndClearLaterAsync(string text, int seconds, CancellationToken cancellationToken)
    {
        var (copy, _, _) = ToolsFor(Current);
        await RunAsync(copy, text, cancellationToken);
        StartClearing(Hash(text), seconds);
    }

    /// <summary>The hidden verb: waits, then clears the clipboard if it still holds the text whose hash came on stdin.</summary>
    public static async Task<int> RunClearAsync(IReadOnlyList<string> args)
    {
        var expected = (await Console.In.ReadLineAsync())?.Trim() ?? string.Empty;
        if (args.Count < 1 || !int.TryParse(args[0], out var seconds) || expected.Length == 0)
        {
            return 2;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var (_, paste, clear) = ToolsFor(Current);
        if (ShouldClear(await ReadAsync(paste), expected))
        {
            await RunAsync(clear, string.Empty, CancellationToken.None);
        }

        return 0;
    }

    /// <summary>Whether the clipboard still holds the copied secret: only then is it cleared.</summary>
    /// <remarks>A line ending one tool adds when it prints the clipboard (PowerShell does) is not part of what was copied.</remarks>
    public static bool ShouldClear(string? clipboard, string expectedHash) =>
        clipboard is not null
        && (string.Equals(Hash(clipboard), expectedHash, StringComparison.Ordinal)
            || string.Equals(Hash(clipboard.TrimEnd('\r', '\n')), expectedHash, StringComparison.Ordinal));

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void StartClearing(string hash, int seconds)
    {
        var self = Environment.ProcessPath ?? throw new SapassException("Cannot tell this tool's own path to clear the clipboard later.");
        var start = new ProcessStartInfo(self) { RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add(ClearVerb);
        start.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new SapassException("Could not start clearing the clipboard.");
        process.StandardInput.WriteLine(hash);
        process.StandardInput.Close();
    }

    private static async Task RunAsync(Tool tool, string input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool.FileName) { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in tool.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new SapassException($"Could not start {tool.FileName}.");
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new SapassException($"{tool.FileName} could not reach the clipboard (exit {process.ExitCode}).");
            }
        }
        catch (Win32Exception)
        {
            throw new SapassException($"The clipboard needs {tool.FileName}, which is not installed.");
        }
    }

    private static async Task<string?> ReadAsync(Tool tool)
    {
        var start = new ProcessStartInfo(tool.FileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in tool.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? text : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
