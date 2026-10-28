using System.ComponentModel;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class FindSettings : StoreSettings
{
    [CommandArgument(0, "<text>")]
    [Description("What to look for in the paths, case-insensitively.")]
    public string Text { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Text)
        ? ValidationResult.Error("Say what to look for: apass find <text>.")
        : ValidationResult.Success();
}

/// <summary>
/// Prints every path containing <c>text</c>, case-insensitively — names only, so no key and no PIN (ADR 0964).
/// </summary>
public sealed class FindCommand : AsyncCommand<FindSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FindSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        foreach (var row in rows
            .Where(r => r.Path.Contains(settings.Text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine(row.IsFolder ? $"{row.Path}/" : row.Path);
        }

        return 0;
    }
}
