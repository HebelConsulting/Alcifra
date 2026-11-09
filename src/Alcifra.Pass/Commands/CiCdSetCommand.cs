using System.ComponentModel;
using Alcifra.ApiClient.CiCd;
using Alcifra.ConsoleLogging;
using Alcifra.Pass.Infrastructure;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

public sealed class CiCdSetSettings : CommandSettings
{
    [CommandArgument(0, "<store>")]
    [Description("The CI/CD store, by name.")]
    public string Store { get; init; } = string.Empty;

    [CommandArgument(1, "<entry>")]
    [Description("The secret's name: made when it does not exist, replaced whole when it does.")]
    public string Entry { get; init; } = string.Empty;

    [CommandOption("--target <TARGET>")]
    [Description("Where it is set: github:owner/name or github:owner/name@environment. Repeatable.")]
    public string[] Targets { get; init; } = [];

    [CommandOption("--secret <KEY=VALUE>")]
    [Description("A secret value; KEY=- asks for it with no echo, so it never lands in the shell's history. Repeatable.")]
    public string[] Secrets { get; init; } = [];

    [CommandOption("--from-file <KEY=PATH>")]
    [Description("A secret that is a file, kept as its raw bytes and sent base64-encoded. Repeatable.")]
    public string[] Files { get; init; } = [];

    [CommandOption("--variable <KEY=VALUE>")]
    [Description("A variable: not secret, so not sealed. Repeatable.")]
    public string[] Variables { get; init; } = [];

    public override ValidationResult Validate() =>
        Secrets.Concat(Files).Concat(Variables).FirstOrDefault(p => Alcifra.Presentation.CiCdEntryRules.SplitVariable(p) is null) is { } bad
            ? ValidationResult.Error($"'{bad}' is not KEY=VALUE.")
            : Secrets.Length + Files.Length + Variables.Length == 0
                ? ValidationResult.Error("Give at least one --secret, --from-file or --variable.")
                : ValidationResult.Success();
}

/// <summary>
/// Makes a CI/CD secret, or replaces one whole (ADR 1084): its targets, secret values and variables as given, sealed to the
/// store's audience in one version. Replacing opens the current one first, so the change carries the tag it was read under.
/// </summary>
public sealed class CiCdSetCommand(ILogger<CiCdSetCommand> logger) : AsyncCommand<CiCdSetSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CiCdSetSettings settings, CancellationToken cancellationToken)
    {
        var secrets = new List<CiCdSecretValue>();
        foreach (var (name, value) in settings.Secrets.Select(s => Alcifra.Presentation.CiCdEntryRules.SplitVariable(s)!.Value))
        {
            var text = value == "-"
                ? SecretInput.ReadHidden($"Value of {name}:", PassLog.For(nameof(SecretInput))) ?? throw new PassException($"No terminal to ask for {name} at.")
                : value;
            secrets.Add(new CiCdSecretValue(name, System.Text.Encoding.UTF8.GetBytes(text), IsFile: false));
        }

        foreach (var (name, path) in settings.Files.Select(f => Alcifra.Presentation.CiCdEntryRules.SplitVariable(f)!.Value))
        {
            secrets.Add(new CiCdSecretValue(name, await File.ReadAllBytesAsync(path, cancellationToken), IsFile: true));
        }

        var content = new CiCdEntryContent(settings.Targets,
            secrets, [.. settings.Variables.Select(v => Alcifra.Presentation.CiCdEntryRules.SplitVariable(v)!.Value).Select(v => new CiCdVariable(v.Name, v.Value))]);
        if (content.Problems is { Count: > 0 } problems)
        {
            throw new PassException($"'{settings.Entry}' cannot be saved: {string.Join(", ", problems)}.");
        }

        using var session = Session.FromEnvironment(new CardOpener());
        var store = await CiCdStores.StoreAsync(session, settings.Store, cancellationToken);
        if (CiCdStores.Secret(store, settings.Entry) is { } existing)
        {
            var opened = await session.CiCd.OpenAsync(await session.CiCd.GetEntryAsync(existing.Href, cancellationToken), cancellationToken);
            await session.CiCd.SaveAsync(opened, content, cancellationToken);
            logger.LogInformation("Replaced {Entry} in {Store}: {Secrets} secret(s), {Variables} variable(s), {Targets} target(s).",
                settings.Entry, store.Label, secrets.Count, settings.Variables.Length, settings.Targets.Length);
        }
        else
        {
            await session.CiCd.CreateAsync(store, settings.Entry, content, cancellationToken);
            logger.LogInformation("Made {Entry} in {Store}: {Secrets} secret(s), {Variables} variable(s), {Targets} target(s).",
                settings.Entry, store.Label, secrets.Count, settings.Variables.Length, settings.Targets.Length);
        }

        return 0;
    }
}
