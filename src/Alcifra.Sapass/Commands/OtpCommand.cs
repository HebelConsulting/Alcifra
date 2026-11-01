using System.ComponentModel;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class OtpSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    [CommandOption("--code-only")]
    [Description("Print the code alone, without the line saying how long it stays valid: $(apass otp <entry> --code-only).")]
    public bool CodeOnly { get; init; }

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: apass otp <entry>.")
        : ValidationResult.Success();
}

/// <summary>
/// Prints an entry's CURRENT verification code (ADR 1013) and, on a second line, how long it stays valid
/// (<c>valid 17 s</c>), so a person can tell whether to wait for the next one (owner, 2026-11-01). <c>--code-only</c>
/// prints the digits alone on one line — the shape of pass's own <c>otp</c> extension — so
/// <c>$(apass otp Mail/Banking --code-only)</c> takes the code and nothing else. The setup behind it is never printed;
/// the code is computed by the one shared rule every client uses.
/// </summary>
/// <remarks>
/// The entry is opened with the card like <c>apass show</c>; an entry without a verification code, or one whose setup
/// does not read, is refused by name rather than printing nothing.
/// </remarks>
public sealed class OtpCommand : AsyncCommand<OtpSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, OtpSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        var row = StoreCatalog.Find(rows, settings.Entry);
        if (row.IsFolder)
        {
            throw new SapassException($"'{row.Path}' is a folder; name an entry.");
        }

        var entry = await session.Passwords.GetEntryAsync(row.Href, cancellationToken);
        var opened = await session.Passwords.OpenAsync(entry, cancellationToken);
        var setup = opened.Content.VerificationCode
            ?? throw new SapassException($"'{row.Path}' has no verification code.");
        if (!VerificationCodeSetup.TryParse(setup, out var parsed))
        {
            throw new SapassException($"'{row.Path}' has a verification code that cannot be read; replace it in the desktop or phone app.");
        }

        // Data output (ADR 0906): the code valid now and, unless asked not to, how long it stays valid.
        foreach (var line in Lines(parsed!.Current(TimeProvider.System), settings.CodeOnly))
        {
            Console.Out.WriteLine(line);
        }

        return 0;
    }

    /// <summary>The code, then its seconds left unless <paramref name="codeOnly"/>.</summary>
    internal static IReadOnlyList<string> Lines(VerificationCode code, bool codeOnly) =>
        codeOnly ? [code.Value] : [code.Value, $"valid {code.SecondsLeft} s"];
}
