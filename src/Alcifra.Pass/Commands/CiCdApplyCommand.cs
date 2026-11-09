using System.ComponentModel;
using Alcifra.ApiClient.CiCd;
using Alcifra.Pass.Infrastructure;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

public sealed class CiCdApplySettings : CommandSettings
{
    [CommandArgument(0, "<store>")]
    [Description("The CI/CD store, by name, or by its full 'Parent / Name' where names repeat.")]
    public string Store { get; init; } = string.Empty;

    [CommandOption("--dry-run")]
    [Description("Report what is missing or stale on each target and set nothing. Needs no key.")]
    public bool DryRun { get; init; }

    [CommandOption("--target <TARGET>")]
    [Description("Only this target, e.g. github:HebelConsulting/Alcifra.")]
    public string? Target { get; init; }
}

/// <summary>
/// Sets a CI/CD store's secrets and variables on their targets (ADR 1084): what is missing, or older on the target than
/// the secret's last change, and nothing else. One PIN for the run; each value goes to <c>gh</c> on its standard input and
/// is never printed. <c>--dry-run</c> reads only plain index data and what the platform shows, so it needs no key.
/// </summary>
public sealed class CiCdApplyCommand(ILogger<CiCdApplyCommand> logger) : AsyncCommand<CiCdApplySettings>
{
    /// <summary>The platform; a test hands in its own.</summary>
    internal static Func<ILogger, ICiCdPlatform> Platform { get; set; } = log => new GitHubCli(log);

    protected override async Task<int> ExecuteAsync(CommandContext context, CiCdApplySettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(settings.DryRun ? null : new CardOpener());
        var store = await CiCdStores.StoreAsync(session, settings.Store, cancellationToken);
        var entries = store.Items.Where(i => !i.IsFolder && i.CiCd is not null).ToList();
        var platform = Platform(logger);

        var targets = new Dictionary<string, CiCdTargetState>(StringComparer.Ordinal);
        foreach (var target in entries.SelectMany(e => e.CiCd!.Targets).Distinct(StringComparer.Ordinal)
            .Where(t => settings.Target is null || string.Equals(t, settings.Target, StringComparison.Ordinal)))
        {
            targets[target] = await platform.ReadAsync(target, cancellationToken);
        }

        var steps = CiCdPlan.For([.. entries.Select(e => (e.Name, e.CiCd!))], targets, settings.Target);
        var due = steps.Where(s => s.State != CiCdKeyState.Unchanged).ToList();
        foreach (var step in steps)
        {
            logger.LogInformation("{Entry} {Key} on {Target}: {State}", step.Entry, step.Key, step.Target,
                step.State switch { CiCdKeyState.Missing => "missing", CiCdKeyState.Stale => "stale", _ => "unchanged" });
        }

        if (settings.DryRun || due.Count == 0)
        {
            logger.LogInformation("{Due} of {Total} to set{DryRun}.", due.Count, steps.Count, settings.DryRun ? " (dry run: nothing set)" : string.Empty);
            return 0;
        }

        // Opened once per entry that has a secret to set: one PIN, held by the card session for the run.
        var opened = new Dictionary<string, OpenedCiCdEntry>(StringComparer.Ordinal);
        foreach (var step in due)
        {
            var row = entries.Single(e => e.Name == step.Entry);
            var entry = await session.CiCd.GetEntryAsync(row.Href, cancellationToken);
            if (step.IsVariable)
            {
                var value = Alcifra.Presentation.CiCdEntryRules.SplitVariable(row.CiCd!.Variables.First(v => v.StartsWith($"{step.Key}=", StringComparison.Ordinal)))!.Value.Value;
                await platform.SetVariableAsync(step.Target, step.Key, value, cancellationToken);
            }
            else
            {
                if (!opened.TryGetValue(step.Entry, out var content))
                {
                    content = await session.CiCd.OpenAsync(entry, cancellationToken);
                    opened[step.Entry] = content;
                }

                var secret = content.Content.Secrets.Single(s => s.Name == step.Key);
                // A file is sent base64-encoded, as a CI secret holds text; its bytes stay raw in the archive.
                var sent = secret.IsFile ? System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(secret.Value)) : secret.Value;
                await platform.SetSecretAsync(step.Target, step.Key, sent, cancellationToken);
            }

            await session.CiCd.ReportAsync(entry, "applied", step.Key, step.Target, cancellationToken);
            logger.LogInformation("{Entry} {Key} on {Target}: set", step.Entry, step.Key, step.Target);
        }

        logger.LogInformation("{Set} set, {Unchanged} unchanged.", due.Count, steps.Count - due.Count);
        return 0;
    }
}
