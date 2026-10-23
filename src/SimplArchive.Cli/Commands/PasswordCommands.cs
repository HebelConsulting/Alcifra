using System.ComponentModel;
using Microsoft.Extensions.Logging;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class PasswordStoreCreateSettings : TenantSessionSettings
{
    [CommandOption("--parent <ID>")]
    [Description("The folder or repository to create the store in, by id.")]
    public string Parent { get; init; } = string.Empty;

    [CommandOption("--name <NAME>")]
    [Description("The store's name. An existing store of exactly this name there is reused, not duplicated.")]
    public string Name { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Guid.TryParse(Parent, out _) => ValidationResult.Error("Name the folder to create the store in with --parent <ID>."),
        _ when string.IsNullOrWhiteSpace(Name) => ValidationResult.Error("Give the store a --name <NAME>."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Creates a shared password store (ADR 0964) in a folder, or finds the one of that name there, and prints its id on
/// stdout (ADR 0966). Who may read it is then an ordinary grant: <c>acl break-inheritance</c>, <c>acl set</c>.
/// </summary>
public sealed class PasswordStoreCreateCommand(ILogger<PasswordStoreCreateCommand> logger) : AsyncCommand<PasswordStoreCreateSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordStoreCreateSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var (id, created) = await PasswordStoreSurface.FindOrCreateStoreAsync(
            new SimplArchiveApi(http), Guid.Parse(settings.Parent), settings.Name, cancellationToken);
        if (created)
        {
            logger.LogInformation("Created password store {Name}.", settings.Name);
        }
        else
        {
            logger.LogInformation("Password store {Name} already exists; unchanged.", settings.Name);
        }

        // Data output (ADR 0906): the id alone on stdout, for `id=$(saconsole passwords store create …)`.
        Console.Out.WriteLine(id.ToString("D"));
        return 0;
    }
}

public sealed class PasswordStoreShowSettings : TenantSessionSettings
{
    [CommandOption("--store <ID>")]
    [Description("The store, by id.")]
    public string Store { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Guid.TryParse(Store, out _) => ValidationResult.Error("Name the store with --store <ID>."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Reports a store: its sealing state, and its folders and entries by NAME (names are in the clear; nothing is opened).
/// The entry names alone go to stdout, one per line, so a script can verify what is there (ADR 0966).
/// </summary>
public sealed class PasswordStoreShowCommand(ILogger<PasswordStoreShowCommand> logger) : AsyncCommand<PasswordStoreShowSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordStoreShowSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var store = await PasswordStoreSurface.StoreAsync(
            new PasswordStoreClient(ApiCore.Over(http)), Guid.Parse(settings.Store), cancellationToken);

        logger.LogInformation(
            "{Name}: {Entries} entr(ies), {Folders} folder(s), sealed to {Certificates} reader certificate(s){Reseal}.",
            store.Name, store.Items.Count(i => !i.IsFolder), store.Items.Count(i => i.IsFolder), store.ReaderCertificateCount,
            store.UrgentReseal ? "; URGENT: an entry is still sealed to a revoked certificate" : store.NeedsReseal ? "; needs a re-seal" : string.Empty);
        foreach (var folder in store.Items.Where(i => i.IsFolder))
        {
            logger.LogInformation("folder: {Name}", folder.Name);
        }

        // Data output (ADR 0906): the entry names, one per line, for a script that checks what the store holds.
        Console.Out.Write(string.Concat(store.Items.Where(i => !i.IsFolder).Select(i => $"{i.Name}\n")));
        return 0;
    }
}

public sealed class PasswordEntryCreateSettings : TenantSessionSettings
{
    [CommandOption("--store <ID>")]
    [Description("The store to create the entry in, by id.")]
    public string Store { get; init; } = string.Empty;

    [CommandOption("--name <NAME>")]
    [Description("The entry's name (in the clear). An existing entry of exactly this name is left alone: no new version.")]
    public string Name { get; init; } = string.Empty;

    // Not `--url`: that is every command's installation address (ApiSettings).
    [CommandOption("--entry-url <URL>")]
    [Description("The entry's URL field (the address the login is for).")]
    public string? EntryUrl { get; init; }

    [CommandOption("--user <NAME>")]
    [Description("The entry's user name field.")]
    public string? UserName { get; init; }

    [CommandOption("--password-stdin")]
    [Description("Read the password from stdin. Required: never an option, so it cannot land in shell history or a process list.")]
    public bool PasswordFromStdin { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Guid.TryParse(Store, out _) => ValidationResult.Error("Name the store with --store <ID>."),
        _ when string.IsNullOrWhiteSpace(Name) => ValidationResult.Error("Give the entry a --name <NAME>."),
        _ when !PasswordFromStdin => ValidationResult.Error(
            "The password is read from stdin only: pass --password-stdin and pipe it in (printf '%s' \"$value\" | saconsole …)."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Creates an entry in a password store, sealed on THIS machine to every certificate the store's readers hold (ADR
/// 0964), or leaves the one of that name alone; prints its id on stdout (ADR 0966). The password arrives on stdin
/// only and is never logged.
/// </summary>
public sealed class PasswordEntryCreateCommand(ILogger<PasswordEntryCreateCommand> logger) : AsyncCommand<PasswordEntryCreateSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordEntryCreateSettings settings, CancellationToken cancellationToken)
    {
        // Read FIRST, whatever happens next: a writer piping into a tool that exits without reading gets SIGPIPE, and
        // under `set -o pipefail` that fails the pipeline even when the entry was simply already there.
        var secret = await PasswordInput.ReadAsync(Console.In, cancellationToken);

        using var http = CertificateEndpoint.Client(settings);
        var (id, outcome) = await PasswordStoreSurface.FindOrCreateEntryAsync(
            new PasswordStoreClient(ApiCore.Over(http)),
            Guid.Parse(settings.Store),
            settings.Name,
            PasswordEntryContent.Of(secret, settings.UserName, settings.EntryUrl),
            cancellationToken);

        var outcomeText = outcome switch
        {
            PasswordStoreSurface.EntryOutcome.Created => "created, sealed to the store's readers",
            PasswordStoreSurface.EntryOutcome.Completed => "existed without content (an interrupted run); filed now, sealed to the store's readers",
            _ => "already exists; unchanged",
        };
        logger.LogInformation("Entry {Name}: {Outcome}.", settings.Name, outcomeText);

        // Data output (ADR 0906): the id alone on stdout.
        Console.Out.WriteLine(id.ToString("D"));
        return 0;
    }
}

/// <summary>A secret read from stdin, the one way a password reaches this tool (ADR 0886).</summary>
internal static class PasswordInput
{
    /// <summary>Everything on <paramref name="input"/>, without the line break a here-string or <c>echo</c> adds.</summary>
    /// <exception cref="CliException">Nothing arrived.</exception>
    internal static async Task<string> ReadAsync(TextReader input, CancellationToken cancellationToken)
    {
        var read = (await input.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n');
        return read.Length > 0
            ? read
            : throw new CliException("No password arrived on stdin. Pipe it in: printf '%s' \"$value\" | saconsole passwords entry create …");
    }
}
