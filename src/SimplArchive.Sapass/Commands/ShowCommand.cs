using System.ComponentModel;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class ShowSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: sapass show <entry>.")
        : ValidationResult.Success();
}

/// <summary>
/// Prints an entry's password on the FIRST line and its other fields after it — pass's shape, so
/// <c>sapass show x | head -1</c> gives the password alone.
/// </summary>
/// <remarks>
/// <b>This is the one command that touches the key.</b> The envelope is opened on this machine by the card in the
/// reader, through the same opener the desktop uses; the PIN is typed at the terminal and is not kept — the card
/// session holds the LOGIN, not the secret (ADR 0832).
/// </remarks>
public sealed class ShowCommand : AsyncCommand<ShowSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ShowSettings settings, CancellationToken cancellationToken)
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

        // Data output (ADR 0906): the password first, then the entry's fields, one per line.
        Console.Out.WriteLine(opened.Content.Password);
        foreach (var field in opened.Content.Fields)
        {
            Console.Out.WriteLine($"{field.Name}: {field.Value}");
        }

        return 0;
    }
}
