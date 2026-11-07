using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.Login;
using Alcifra.Pass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

public sealed class LoginSettings : CommandSettings
{
    [CommandOption("--url <URL>")]
    [Description("Base URL of the installation, e.g. https://archive.example.com.")]
    public string Url { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Url)
        ? ValidationResult.Error("Name the installation with --url <URL>.")
        : ValidationResult.Success();
}

/// <summary>
/// Signs a user in through the device authorization grant (RFC 8628) and keeps the session in the file both tools read
/// (ADR 1071). Nothing is printed to stdout, so <c>eval "$(apass login …)"</c> from before that still works.
/// </summary>
public sealed class LoginCommand(ILogger<LoginCommand> logger) : AsyncCommand<LoginSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.Url.TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri($"{url}/") };
        var flow = new DeviceFlow(http, PassIdentity.ClientId);

        var authorization = await flow.RequestAsync(cancellationToken);
        logger.LogInformation("Code            {UserCode}", authorization.UserCode);
        logger.LogInformation("Approve it at   {VerificationUri}", authorization.VerificationUriComplete ?? authorization.VerificationUri);
        if (authorization.VerificationUriComplete is not null)
        {
            logger.LogInformation("or enter it at  {VerificationUri}", authorization.VerificationUri);
        }

        logger.LogInformation("Check the code on that page matches the one above before approving. Waiting for approval…");
        var session = await flow.PollForSessionAsync(authorization, _ => { }, cancellationToken);

        CliSession.Default.Save(url, PassIdentity.ClientId, session);
        logger.LogInformation("Signed in to {Url}.", url);
        if (CliSession.ShellOverrideWarning(Environment.GetEnvironmentVariable(Session.UrlVariable), Environment.GetEnvironmentVariable(Session.TokenVariable), url) is { } warning)
        {
            logger.LogWarning("{Warning}", warning);
        }

        return 0;
    }
}
