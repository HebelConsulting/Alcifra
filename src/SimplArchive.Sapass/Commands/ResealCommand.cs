using System.ComponentModel;
using Microsoft.Extensions.Logging;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

/// <summary>
/// Re-seals a store whose readers changed (ADR 0964): one PIN opens every entry, and each is sealed again to the
/// readers' current certificates.
/// </summary>
/// <remarks>
/// <para>
/// The server marks a store "needs re-seal" when an entry is not sealed to exactly its readers' certificates; the
/// next reader who opens it may act, which is the design's whole point — it needs no administrator.
/// </para>
/// <para>
/// Each entry is a separate change carrying the ETag it was read under (ADR 0794), so one somebody changed meanwhile
/// is SKIPPED and reported rather than overwritten. Entries this key cannot open are named with the reason.
/// </para>
/// </remarks>
public sealed class ResealCommand(ILogger<ResealCommand> logger) : AsyncCommand<StoreSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, StoreSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);

        if (!store.NeedsReseal)
        {
            logger.LogInformation("{Store} is already sealed to its readers' current certificates.", store.Name);
            return 0;
        }

        if (!store.CanReseal)
        {
            throw new SapassException(
                $"'{store.Name}' needs a re-seal, and your rights there do not allow it — the store's readers with edit rights can.");
        }

        var outcome = await session.Passwords.ResealAsync(store, cancellationToken);
        logger.LogInformation(
            "{Store}: {Resealed} entr(ies) sealed again, {Skipped} changed meanwhile (reload and reseal), {Failed} this key could not open.",
            store.Name, outcome.Resealed.Count, outcome.SkippedChanged.Count, outcome.Failed.Count);
        foreach (var (name, reason) in outcome.Failed)
        {
            logger.LogWarning("{Entry} was not reopened: {Reason}", name, reason);
        }

        return 0;
    }
}
