using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.PasswordStore;
using System.ComponentModel;
using Alcifra.Pass.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

public sealed class LsSettings : StoreSettings
{
    [CommandArgument(0, "[path]")]
    [Description("The folder to list, e.g. Mail/Banking. Empty lists the store's root.")]
    public string? Path { get; init; }
}

/// <summary>
/// Lists what a folder holds: folders with a trailing slash, then entries, in the order the tree holds them.
/// </summary>
/// <remarks>
/// The names come from the flat listing (ADR 0207) and are in the clear (ADR 0964), so nothing here touches the
/// card or asks for a PIN.
/// </remarks>
public sealed class LsCommand(ILogger<LsCommand> logger) : AsyncCommand<LsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LsSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        var path = PasswordTree.Normalise(settings.Path);
        if (path.Length > 0)
        {
            // A folder named on the command line must exist, and must BE a folder: listing an entry's "contents"
            // would silently print nothing and look like an empty folder.
            var folder = StoreCatalog.Find(rows, path);
            if (!folder.IsFolder)
            {
                throw new PassException($"'{path}' is an entry, not a folder.");
            }
        }

        foreach (var row in PasswordTree.Children(rows, path))
        {
            Console.Out.WriteLine(row.IsFolder ? $"{row.Name}/" : row.Name);
        }

        // An empty default store printed nothing at all, which read as "you have no passwords" while they sat in a shared
        // store (owner, 2026-11-10). Said on stderr, so a script reading the listing is unaffected.
        if (rows.Count == 0 && string.IsNullOrWhiteSpace(settings.Store)
            && StoreCatalog.CompletionNames([.. (await session.Passwords.ListStoresAsync(cancellationToken)).Stores.Where(s => !s.IsPersonal)], offersMyPasswords: false)
                is { Count: > 0 } others)
        {
            logger.LogInformation("{Hint}", EmptyDefaultHint(others));
        }

        return 0;
    }

    /// <summary>What an empty My Passwords says, naming the stores <c>--store</c> accepts.</summary>
    internal static string EmptyDefaultHint(IReadOnlyList<string> stores) =>
        $"My Passwords is empty. The stores you may read: {string.Join(", ", stores)}. List one with: apass ls -s \"<name>\"";
}
