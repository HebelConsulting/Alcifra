using System.ComponentModel;
using Alcifra.Pass.Infrastructure;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

public sealed class CiCdShowSettings : CommandSettings
{
    [CommandArgument(0, "<store>")]
    [Description("The CI/CD store, by name.")]
    public string Store { get; init; } = string.Empty;

    [CommandArgument(1, "<entry>")]
    [Description("The secret's name in the store.")]
    public string Entry { get; init; } = string.Empty;

    [CommandOption("--key <KEY>")]
    [Description("The key to show; may be left out when the entry holds one secret.")]
    public string? Key { get; init; }

    [CommandOption("--save <PATH>")]
    [Description("Write the value to this file, as its raw bytes, instead of printing it (a .p12, say).")]
    public string? Save { get; init; }
}

/// <summary>
/// One value of a CI/CD secret, opened with the key (ADR 1084): printed, or saved as the file it was. The reveal is
/// reported to the server by entry and key, never the value.
/// </summary>
public sealed class CiCdShowCommand(ILogger<CiCdShowCommand> logger) : AsyncCommand<CiCdShowSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CiCdShowSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var store = await CiCdStores.StoreAsync(session, settings.Store, cancellationToken);
        var row = CiCdStores.Secret(store, settings.Entry)
            ?? throw new PassException($"'{store.Label}' holds no secret named '{settings.Entry}'.");
        var keys = row.CiCd?.SecretKeys ?? [];
        var key = settings.Key ?? (keys.Count == 1 ? keys[0]
            : throw new PassException($"'{row.Name}' holds {keys.Count} secrets; name one with --key: {string.Join(", ", keys)}."));
        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            throw new PassException($"'{row.Name}' has no secret '{key}'. Its keys: {string.Join(", ", keys)}.");
        }

        var entry = await session.CiCd.GetEntryAsync(row.Href, cancellationToken);
        var value = (await session.CiCd.OpenAsync(entry, cancellationToken)).Content.Secrets.Single(s => s.Name == key);
        await session.CiCd.ReportAsync(entry, "revealed", key, target: null, cancellationToken);

        if (settings.Save is { Length: > 0 } path)
        {
            await File.WriteAllBytesAsync(path, value.Value, cancellationToken);
            logger.LogInformation("Saved {Key} of {Entry} to {Path} ({Bytes} bytes).", key, row.Name, path, value.Value.Length);
            return 0;
        }

        if (value.IsFile)
        {
            throw new PassException($"'{key}' is a file; write it out with --save <path>.");
        }

        // DATA output (ADR 0906): the value alone, for a script to capture.
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(value.Value));
        return 0;
    }
}
