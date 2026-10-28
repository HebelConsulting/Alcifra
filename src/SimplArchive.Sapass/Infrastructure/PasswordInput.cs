namespace SimplArchive.Sapass.Infrastructure;

/// <summary>Where a NEW password comes from: stdin (a pipe) or the terminal, asked twice with no echo.</summary>
/// <remarks>
/// Never an argument: a secret on argv is in the process list and in the shell's history (ADR 0886). A pipe is the
/// scripted form; the double prompt is the human one.
/// </remarks>
internal static class PasswordInput
{
    public static async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            var read = (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
            return read.Length > 0
                ? read
                : throw new SapassException("No password arrived on stdin. Pipe it in: printf '%s' \"$pw\" | apass insert …");
        }

        var first = SecretInput.ReadHidden("Password:");
        var second = SecretInput.ReadHidden("Repeat:");
        if (first is null || second is null)
        {
            throw new SapassException("There is no terminal to ask at; pipe the password in instead.");
        }

        return string.Equals(first, second, StringComparison.Ordinal)
            ? first
            : throw new SapassException("The two passwords differ; nothing was stored.");
    }
}
