using SimplArchive.ApiClient.PasswordStore;
using System.ComponentModel;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

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
public sealed class LsCommand : AsyncCommand<LsSettings>
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
                throw new SapassException($"'{path}' is an entry, not a folder.");
            }
        }

        foreach (var row in PasswordTree.Children(rows, path))
        {
            Console.Out.WriteLine(row.IsFolder ? $"{row.Name}/" : row.Name);
        }

        return 0;
    }
}
