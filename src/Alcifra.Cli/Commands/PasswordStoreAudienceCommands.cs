using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class PasswordStoreAudienceShowSettings : TenantSessionSettings
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

public sealed class PasswordStoreAudienceAppointSettings : TenantSessionSettings
{
    [CommandOption("--store <ID>")]
    [Description("The store, by id.")]
    public string Store { get; init; } = string.Empty;

    [CommandOption("--manager <NAME>")]
    [Description("The reader to appoint as manager, by display name.")]
    public string Manager { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Guid.TryParse(Store, out _) => ValidationResult.Error("Name the store with --store <ID>."),
        _ when string.IsNullOrWhiteSpace(Manager) => ValidationResult.Error("Name the reader to appoint with --manager <NAME>."),
        _ => ValidationResult.Success(),
    };
}

public sealed class PasswordStoreAudienceSetSettings : TenantSessionSettings
{
    [CommandOption("--store <ID>")]
    [Description("The store, by id.")]
    public string Store { get; init; } = string.Empty;

    [CommandOption("--manager <NAME>")]
    [Description("A reader who manages the audience, by display name. Repeatable; at least one.")]
    public string[] Managers { get; init; } = [];

    [CommandOption("--member <NAME>")]
    [Description("A reader in the audience, by display name. Repeatable.")]
    public string[] Members { get; init; } = [];

    [CommandOption("--group <NAME>")]
    [Description("A group whose readers are in the audience, by name. Repeatable.")]
    public string[] Groups { get; init; } = [];

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Guid.TryParse(Store, out _) => ValidationResult.Error("Name the store with --store <ID>."),
        _ when Managers.Length == 0 => ValidationResult.Error("Name at least one --manager <NAME>: somebody must stay in charge."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Replaces who a store is encrypted for (ADR 0983), by name: the managers, the members and the groups, as one list.
/// For a store NOBODY manages yet — the one a seed just created (ADR 0966) — a service account may do this unattended;
/// afterwards only its managers change it (in apass or the clients, which re-seal). Every name must read the store.
/// </summary>
/// <remarks>Run BEFORE filing entries: what is filed is sealed to the audience as it is then.</remarks>
public sealed class PasswordStoreAudienceSetCommand(ILogger<PasswordStoreAudienceSetCommand> logger)
    : AsyncCommand<PasswordStoreAudienceSetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordStoreAudienceSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var core = ApiCore.Over(http);
        var store = await PasswordStoreSurface.StoreAsync(new PasswordStoreClient(core), Guid.Parse(settings.Store), cancellationToken);
        var audiences = new ContentAudienceClient(core);
        var audience = await audiences.GetAsync(store.Links, cancellationToken);
        if (!audience.CanManage)
        {
            throw new CliException($"'{store.Name}' is managed by people now; only its managers change its audience (apass audience).");
        }

        var known = audience.Members.Select(m => (m.DisplayName, m.IsGroup, Change: AudienceChange.Of(m)))
            .Concat(audience.Candidates.Select(c => (c.DisplayName, c.IsGroup, Change: AudienceChange.Of(c))))
            .ToList();
        AudienceChange Find(string name, bool group) =>
            known.Where(k => k.IsGroup == group && string.Equals(k.DisplayName, name, StringComparison.CurrentCultureIgnoreCase)).ToList() switch
            {
                [var one] => one.Change,
                [] => throw new CliException($"'{name}' does not read '{store.Name}': grant read access first (alcifra acl set)."),
                _ => throw new CliException($"'{name}' names more than one {(group ? "group" : "person")} here."),
            };

        var rows = settings.Managers.Select(n => Find(n, false) with { IsManager = true })
            .Concat(settings.Members.Except(settings.Managers, StringComparer.CurrentCultureIgnoreCase).Select(n => Find(n, false) with { IsManager = false }))
            .Concat(settings.Groups.Select(n => Find(n, true) with { IsManager = false }))
            .ToList();
        var saved = await Translated(() => audiences.SaveAsync(audience, rows, cancellationToken));
        logger.LogInformation("{Store} is encrypted for {Members} member(s), managed by {Managers}.",
            store.Name, saved.Recipients.Count, string.Join(", ", settings.Managers));
        return 0;
    }

    private static async Task<T> Translated<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ApiActionException e)
        {
            throw new CliException(e.Message);
        }
    }
}

/// <summary>
/// Reports who a store is encrypted for (ADR 0983): its declared members by name on stdout, one per line with their
/// role, and a summary of who is in effect. Nothing is opened.
/// </summary>
public sealed class PasswordStoreAudienceShowCommand(ILogger<PasswordStoreAudienceShowCommand> logger)
    : AsyncCommand<PasswordStoreAudienceShowSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordStoreAudienceShowSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var core = ApiCore.Over(http);
        var store = await PasswordStoreSurface.StoreAsync(new PasswordStoreClient(core), Guid.Parse(settings.Store), cancellationToken);
        var audience = await new ContentAudienceClient(core).GetAsync(store.Links, cancellationToken);

        logger.LogInformation("{Name}: {Members} member(s) in effect, {Managers} manager(s){Orphan}.",
            store.Name, audience.Recipients.Count, audience.Recipients.Count(r => r.IsManager),
            audience.Recipients.Any(r => r.IsManager) ? string.Empty : "; NO MANAGER — a tenant administrator may appoint one");

        // Data output (ADR 0906): name, tab, role (and "not-in-effect"), one per line, for a script.
        Console.Out.Write(string.Concat(audience.Members.Select(m =>
            $"{m.DisplayName}\t{(m.IsGroup ? "group" : m.IsManager ? "manager" : "member")}{(m.InEffect ? string.Empty : "\tnot-in-effect")}\n")));
        return 0;
    }
}

/// <summary>
/// Appoints a manager to a store left without one (ADR 0983): the tenant administrator's one audience act, run signed in
/// as that administrator (<c>alcifra login</c>). The appointee must read the store; nothing else in the audience
/// changes, and no key is needed — a new member is sealed to by the next re-seal of a member who holds one.
/// </summary>
public sealed class PasswordStoreAudienceAppointCommand(ILogger<PasswordStoreAudienceAppointCommand> logger)
    : AsyncCommand<PasswordStoreAudienceAppointSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PasswordStoreAudienceAppointSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var core = ApiCore.Over(http);
        var store = await PasswordStoreSurface.StoreAsync(new PasswordStoreClient(core), Guid.Parse(settings.Store), cancellationToken);
        var audiences = new ContentAudienceClient(core);
        var audience = await audiences.GetAsync(store.Links, cancellationToken);
        if (!audience.CanAppointManager)
        {
            throw new CliException(audience.Recipients.Any(r => r.IsManager)
                ? $"'{store.Name}' has a manager; only they change its audience."
                : $"Only a tenant administrator signed in as a person may appoint a manager to '{store.Name}' (alcifra login).");
        }

        var member = audience.Members.FirstOrDefault(m => !m.IsGroup && Same(m.DisplayName, settings.Manager));
        var candidate = audience.Candidates.FirstOrDefault(c => !c.IsGroup && Same(c.DisplayName, settings.Manager));
        if (member is null && candidate is null)
        {
            throw new CliException($"'{settings.Manager}' does not read '{store.Name}': grant read access first (alcifra acl set).");
        }

        var rows = audience.Members.Select(AudienceChange.Of).ToList();
        var appointed = member is not null ? AudienceChange.Of(member) : AudienceChange.Of(candidate!);
        rows.RemoveAll(r => r.UserId == appointed.UserId && r.GroupId is null);
        rows.Add(appointed with { IsManager = true });
        await Translated(() => audiences.SaveAsync(audience, rows, cancellationToken));
        logger.LogInformation("Appointed {Manager} as the manager of {Store}.", settings.Manager, store.Name);
        return 0;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    private static async Task<T> Translated<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ApiActionException e)
        {
            throw new CliException(e.Message);
        }
    }
}
