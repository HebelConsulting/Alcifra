using System.Text;
using Microsoft.Extensions.Logging;

namespace SimplArchive.Sapass.Infrastructure;

/// <summary>
/// A secret typed at the terminal with no echo. The one place both the card's PIN and a new password are read, so
/// "never keep the secret, never show it, never log it" is one implementation rather than two that can drift.
/// </summary>
internal static class SecretInput
{
    /// <summary>Reads one hidden line, or null when there is no terminal to ask at (a pipe is not a person).</summary>
    public static string? ReadHidden(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            // Declining is the honest answer: a prompt written into a pipe is a secret nobody saw being asked for.
            return null;
        }

        // The PROMPT goes through the logger (ADR 0906); the secret itself is never written anywhere.
        SapassLog.For(nameof(SecretInput)).LogInformation("{Prompt}", prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return value.ToString();
                case ConsoleKey.Backspace when value.Length > 0:
                    value.Length--;
                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.Escape:
                    break;
                default:
                    value.Append(key.KeyChar);
                    break;
            }
        }
    }
}
