using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Provisions a tenant with its first administrator and repository — a platform-administrator act, because a
/// tenant does not exist yet to administer it (ADR 0206).
/// </summary>
public sealed class TenantCreateCommand(ILogger<TenantCreateCommand> logger) : AsyncCommand<TenantCreateCommand.Settings>
{
    public sealed class Settings : PlatformAdminSettings
    {
        [CommandOption("--name <NAME>")]
        [Description("Tenant name.")]
        public required string Name { get; init; }

        [CommandOption("--admin-email <EMAIL>")]
        [Description("Email of the tenant's first administrator.")]
        public required string AdministratorEmail { get; init; }

        [CommandOption("--admin-name <NAME>")]
        [Description("Display name of that administrator.")]
        public required string AdministratorDisplayName { get; init; }

        [CommandOption("--repository <NAME>")]
        [Description("Name of the tenant's first repository.")]
        public required string RepositoryName { get; init; }

        [CommandOption("--password-stdin")]
        [Description("Read the administrator's initial password from stdin, for a seed that must know it up front. Never an option: it would land in the shell's history. Without it a random one is generated.")]
        public bool PasswordFromStdin { get; init; }

        public override ValidationResult Validate() => this switch
        {
            _ when base.Validate() is { Successful: false } failed => failed,
            { Name: null or "" } => ValidationResult.Error("Missing required option --name."),
            { AdministratorEmail: null or "" } => ValidationResult.Error("Missing required option --admin-email."),
            { AdministratorDisplayName: null or "" } => ValidationResult.Error("Missing required option --admin-name."),
            { RepositoryName: null or "" } => ValidationResult.Error("Missing required option --repository."),
            _ => ValidationResult.Success(),
        };
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        var api = new AlcifraApi(http);

        await api.AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);

        // A seed that must know the password up front (the kiosk's showcase tenants, rebuilt at every reset) reads
        // it from stdin; otherwise the server generates one. Either way it is printed once, below.
        var supplied = settings.PasswordFromStdin
            ? (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n')
            : null;

        // FOLLOWED, not composed (ADR 0543, #1409). The root emits `tenants` only to a platform administrator,
        // so authenticating FIRST is what makes it visible — and a refusal here names the honest cause: either
        // the installation predates the rel, or this principal is not a platform administrator. Composing the
        // path instead would have turned the second case into a 403 from a URL the tool invented.
        var created = await api.PostAsync(
            await new Hypermedia(api).RootHrefAsync("tenants", cancellationToken), new
            {
                name = settings.Name,
                administratorEmail = settings.AdministratorEmail,
                administratorDisplayName = settings.AdministratorDisplayName,
                repositoryName = settings.RepositoryName,
                administratorPassword = supplied,
            }, cancellationToken);

        var administrator = created.GetProperty("tenantAdministrator");
        var password = administrator.GetProperty("password").GetString();

        logger.LogInformation(
            "Tenant created: {Tenant} ({TenantId}), administrator {Administrator}, repository {Repository}.",
            settings.Name, created.GetProperty("id").GetString(), administrator.GetProperty("email").GetString() ?? string.Empty,
            settings.RepositoryName);

        // The Api generates this and returns it ONCE — it is not stored anywhere retrievable, so a caller
        // who loses it has to reset rather than look it up. Saying so is part of printing it.
        logger.LogWarning("The initial administrator password follows on stdout. It is shown once and not recoverable.");

        // Data output (ADR 0906): the password alone on stdout, for `password=$(alcifra tenant create …)`; never logged
        // (ADR 0886's named carve-out is this write, not a log line).
        Console.Out.WriteLine(password ?? "(the installation returned none)");

        return 0;
    }
}
