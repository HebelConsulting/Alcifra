using System.ComponentModel;
using Microsoft.Extensions.Logging;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class RmSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: sapass rm Mail/Banking.")
        : ValidationResult.Success();
}

/// <summary>
/// Deletes an entry's document — the trash, not a shredder: the archive's own delete rules apply (permissions,
/// legal hold, retention), which the server decides and a refusal names.
/// </summary>
/// <remarks>
/// The DELETE carries the entry's ETag, so it removes exactly what was read (ADR 0794); a 412 means somebody changed
/// it meanwhile and the command is retried by a person, not by the tool.
/// </remarks>
public sealed class RmCommand(ILogger<RmCommand> logger) : AsyncCommand<RmSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RmSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        var row = StoreCatalog.Find(rows, settings.Entry);
        if (row.IsFolder)
        {
            throw new SapassException($"'{row.Path}' is a folder; rm removes entries.");
        }

        var entry = await session.Passwords.GetEntryAsync(row.Href, cancellationToken);
        await session.Passwords.DeleteAsync(entry.Links, entry.Name, entry.ETag, cancellationToken);

        logger.LogInformation("Deleted {Entry}.", row.Path);
        return 0;
    }
}
