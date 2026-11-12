using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>Takes a principal's whole entry on a document away (<see cref="AclRemoval"/>).</summary>
public sealed class AclRemoveCommand(ILogger<AclRemoveCommand> logger) : AsyncCommand<AclSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, AclSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Rights))
        {
            throw new CliException("acl remove takes the whole entry away, so it takes no --rights. To take some rights away, use acl revoke.");
        }

        using var http = TenantHttp.Client(settings);
        var removed = await AclRemoval.RemoveAsync(new AlcifraApi(http), settings, cancellationToken);
        var name = AclRemoval.PrincipalName(settings);

        if (removed is null)
        {
            logger.LogInformation("{Principal} holds no entry on this document; nothing to remove.", name);
        }
        else
        {
            logger.LogInformation(
                "{Principal}: entry removed; it held {Rights}.",
                name, removed.Granted.Count > 0 ? string.Join(", ", removed.Granted) : "nothing");
        }

        return 0;
    }
}
