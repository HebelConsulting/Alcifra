using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class GenerateSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking. Missing folders are created.")]
    public string Entry { get; init; } = string.Empty;

    [CommandOption("--length <N>")]
    [Description("How many characters (default 24).")]
    public int Length { get; init; } = Passwords.DefaultLength;

    [CommandOption("--user <NAME>")]
    [Description("The user name this login is for (stored inside the sealed content).")]
    public string? UserName { get; init; }

    [CommandOption("--entry-url <URL>")]
    [Description("The address this login is for (stored inside the sealed content).")]
    public string? EntryUrl { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when string.IsNullOrWhiteSpace(Entry) => ValidationResult.Error("Name the entry: apass generate Mail/Banking."),
        { Length: < 8 } => ValidationResult.Error("--length must be at least 8."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Generates a password from an unambiguous alphabet, files it exactly as <c>insert</c> would, and PRINTS it — the
/// one thing the user cannot recover from the terminal afterwards, so it is the command's data output (ADR 0906).
/// </summary>
public sealed class GenerateCommand(ILogger<GenerateCommand> logger) : AsyncCommand<GenerateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GenerateSettings settings, CancellationToken cancellationToken)
    {
        // The store is asked FIRST (ADR 0982): a store that offers no new entry is refused before a secret exists.
        using var session = Session.FromEnvironment();
        var store = await EntryWriter.StoreForNewEntryAsync(session, settings.Store, cancellationToken);
        var password = Passwords.Generate(settings.Length);

        var content = PasswordEntryContent.Of(password, settings.UserName, settings.EntryUrl);
        var entry = await EntryWriter.CreateAsync(session, store, settings.Entry, content, settings.Refresh, cancellationToken);

        logger.LogInformation("Generated and filed {Entry}, sealed to the store's readers.", entry.Name);

        // Data output (ADR 0906): the generated secret, which exists nowhere the user can read it afterwards.
        Console.Out.WriteLine(password);
        return 0;
    }
}
