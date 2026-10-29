using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class AudienceSettings : StoreSettings
{
    [CommandOption("--add <NAME>")]
    [Description("Add a reader (a person or a group, by name) to the store's audience. Repeatable.")]
    public string[] Add { get; init; } = [];

    [CommandOption("--remove <NAME>")]
    [Description("Remove a person or a group from the audience (and from its managers). Repeatable.")]
    public string[] Remove { get; init; } = [];

    [CommandOption("--manager <NAME>")]
    [Description("Make a person a manager, adding them to the audience if needed. Repeatable.")]
    public string[] Manager { get; init; } = [];

    [CommandOption("--unmanage <NAME>")]
    [Description("Keep a person in the audience but no longer as a manager. Repeatable.")]
    public string[] Unmanage { get; init; } = [];

    public bool Changes => Add.Length + Remove.Length + Manager.Length + Unmanage.Length > 0;
}

/// <summary>
/// Shows or changes who a store is encrypted for (ADR 0983). Without options it prints the audience, one member per
/// line; with options it saves the change and re-seals the store with your card in the same act.
/// </summary>
/// <remarks>
/// <para>
/// Names resolve against the audience's own members and the READERS the server offers as candidates — the same list a
/// picker shows — so a name that is neither is refused with that reason, before anything is saved.
/// </para>
/// <para>
/// The listing is the command's DATA output (ADR 0906): <c>name</c>, a tab, <c>manager</c>, <c>member</c> or
/// <c>group</c>, and <c>not-in-effect</c> for a declared member who lost read access — for a script to read.
/// </para>
/// </remarks>
public sealed class AudienceCommand(ILogger<AudienceCommand> logger) : AsyncCommand<AudienceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AudienceSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var store = await new StoreCatalog(session).StoreAsync(settings.Store, cancellationToken);
        var audience = await new ContentAudienceClient(session.Core).GetAsync(store.Links, cancellationToken);

        if (!settings.Changes)
        {
            foreach (var line in Lines(audience))
            {
                // Data output (ADR 0906): the audience alone on stdout, for a script.
                Console.Out.WriteLine(line);
            }

            return 0;
        }

        if (audience.IsPersonal)
        {
            throw new SapassException($"'{store.Label}' is in your personal space: only you can decrypt it, and that cannot be changed.");
        }

        if (!audience.CanManage)
        {
            throw new SapassException($"Only an audience manager of '{store.Label}' can change who it is encrypted for.");
        }

        var members = Apply(audience, settings);
        var (saved, resealed) = await session.Passwords.ChangeAudienceAsync(store, audience, members, cancellationToken);
        logger.LogInformation("{Store} is now encrypted for {Count} member(s); {Resealed} entr(ies) sealed again.",
            store.Label, saved.Recipients.Count, resealed?.Resealed.Count ?? 0);
        foreach (var missing in saved.WithoutCertificate)
        {
            logger.LogWarning("{Name} has no enrolled hardware key yet: nothing is sealed to them until they enrol one.", missing.DisplayName);
        }

        foreach (var (name, reason) in resealed?.Failed ?? [])
        {
            logger.LogWarning("{Entry} was not reopened: {Reason}", name, reason);
        }

        return 0;
    }

    internal static IEnumerable<string> Lines(ContentAudienceInfo audience) =>
        audience.IsPersonal
            ? audience.Recipients.Select(r => $"{r.DisplayName}\tmanager")
            : audience.Members.Select(m =>
                $"{m.DisplayName}\t{(m.IsGroup ? "group" : m.IsManager ? "manager" : "member")}{(m.InEffect ? string.Empty : "\tnot-in-effect")}");

    /// <summary>The audience after the options, applied in order: remove, add, manager, unmanage.</summary>
    internal static List<AudienceChange> Apply(ContentAudienceInfo audience, AudienceSettings settings)
    {
        var rows = audience.Members.Select(AudienceChange.Of).ToList();
        var names = audience.Members.Select(m => (m.DisplayName, Change: AudienceChange.Of(m)))
            .Concat(audience.Candidates.Select(c => (c.DisplayName, Change: AudienceChange.Of(c))))
            .ToList();

        AudienceChange Find(string name)
        {
            var matches = names.Where(n => string.Equals(n.DisplayName, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            return matches.Count switch
            {
                1 => matches[0].Change,
                0 => throw new SapassException($"'{name}' is neither in the audience nor a reader of this store: grant read access first."),
                _ => throw new SapassException($"'{name}' names more than one person or group here."),
            };
        }

        static bool Same(AudienceChange a, AudienceChange b) => a.UserId == b.UserId && a.GroupId == b.GroupId;

        foreach (var name in settings.Remove)
        {
            var found = Find(name);
            rows.RemoveAll(r => Same(r, found));
        }

        foreach (var name in settings.Add)
        {
            var found = Find(name);
            if (!rows.Any(r => Same(r, found)))
            {
                rows.Add(found with { IsManager = false });
            }
        }

        foreach (var name in settings.Manager)
        {
            var found = Find(name);
            if (found.GroupId is not null)
            {
                throw new SapassException($"'{name}' is a group; a manager is always a person.");
            }

            rows.RemoveAll(r => Same(r, found));
            rows.Add(found with { IsManager = true });
        }

        foreach (var name in settings.Unmanage)
        {
            var found = Find(name);
            var index = rows.FindIndex(r => Same(r, found));
            if (index >= 0)
            {
                rows[index] = rows[index] with { IsManager = false };
            }
        }

        return rows;
    }
}
