using System.ComponentModel;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class PasswordSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: apass password <entry>.")
        : ValidationResult.Success();
}

/// <summary>
/// Prints an entry's password alone on one line (ADR 1024), so <c>$(apass password Mail/Banking)</c> is the password and
/// nothing else — the sibling of <c>apass otp</c>: one command per secret, <c>apass show</c> for reading the whole entry.
/// An entry without a password is refused by name rather than printing an empty line.
/// </summary>
/// <remarks>The entry is opened with the card like <c>apass show</c>; the PIN is typed at the terminal and not kept.</remarks>
public sealed class PasswordCommand : AsyncCommand<PasswordSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PasswordSettings settings, CancellationToken cancellationToken)
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
        if (opened.Content.Password.Length == 0)
        {
            throw new SapassException($"'{row.Path}' has no password.");
        }

        // Data output (ADR 0906): the password alone — the whole point of the command, what a script captures.
        Console.Out.WriteLine(opened.Content.Password);
        return 0;
    }
}
