using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.Login;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>Ends the session both tools share: revoked on the server, gone from this machine (ADR 1071).</summary>
public sealed class LogoutCommand(ILogger<LogoutCommand> logger) : AsyncCommand<CommandSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Outcome}", await CliSession.Default.EndAsync() is { } url ? $"Signed out of {url}." : "No session to end.");
        return 0;
    }
}
