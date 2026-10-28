using System.ComponentModel;
using Microsoft.Extensions.Logging;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class InsertSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking. Missing folders are created.")]
    public string Entry { get; init; } = string.Empty;

    [CommandOption("--user <NAME>")]
    [Description("The user name this login is for (stored inside the sealed content).")]
    public string? UserName { get; init; }

    [CommandOption("--entry-url <URL>")]
    [Description("The address this login is for (stored inside the sealed content).")]
    public string? EntryUrl { get; init; }

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: apass insert Mail/Banking.")
        : ValidationResult.Success();
}

/// <summary>
/// Files a new secret: the password comes from stdin (or is typed twice at the terminal), is sealed ON THIS MACHINE
/// to every certificate the store's readers hold (ADR 0964), and never travels in clear.
/// </summary>
/// <remarks>
/// It creates, and only creates: an existing path is refused rather than replaced, because repointing a secret is an
/// edit that must open the old content with the key and carry the entry's precondition. Success is a message on
/// stderr; stdout stays empty, since there is no data to capture.
/// </remarks>
public sealed class InsertCommand(ILogger<InsertCommand> logger) : AsyncCommand<InsertSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, InsertSettings settings, CancellationToken cancellationToken)
    {
        // The store is asked FIRST (ADR 0982): a store that offers no new entry is refused before a secret exists.
        using var session = Session.FromEnvironment();
        var store = await EntryWriter.StoreForNewEntryAsync(session, settings.Store, cancellationToken);
        var password = await PasswordInput.ReadAsync(cancellationToken);

        var content = PasswordEntryContent.Of(password, settings.UserName, settings.EntryUrl);
        var entry = await EntryWriter.CreateAsync(session, store, settings.Entry, content, settings.Refresh, cancellationToken);

        logger.LogInformation("Filed {Entry}, sealed to the store's readers.", entry.Name);
        return 0;
    }
}
