using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.Login;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

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
/// Signs a user in through the device authorization grant (RFC 8628) and prints the session as shell exports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is written to disk.</b> The session lives in the environment, like alcifra's, so there is no
/// credential file to find on a host and no second place for "which installation am I pointing at?".
/// </para>
/// <para>
/// <b>The exports go to STDOUT and everything a person reads goes to STDERR</b> — that split is what makes
/// <c>eval "$(apass login --url …)"</c> work. The token is not repeated in the readable half: it is on stdout
/// because the shell must read it there, and nowhere else.
/// </para>
/// </remarks>
public sealed class LoginCommand(ILogger<LoginCommand> logger) : AsyncCommand<LoginSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.Url.TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri($"{url}/") };
        var flow = new DeviceFlow(http, SapassIdentity.ClientId);

        var authorization = await flow.RequestAsync(cancellationToken);
        logger.LogInformation("Code            {UserCode}", authorization.UserCode);
        logger.LogInformation("Approve it at   {VerificationUri}", authorization.VerificationUriComplete ?? authorization.VerificationUri);
        if (authorization.VerificationUriComplete is not null)
        {
            logger.LogInformation("or enter it at  {VerificationUri}", authorization.VerificationUri);
        }

        logger.LogInformation("Check the code on that page matches the one above before approving. Waiting for approval…");
        var token = await flow.PollAsync(authorization, _ => { }, cancellationToken);

        // Data output (ADR 0906): the shell reads exactly these two lines.
        Console.Out.WriteLine($"export {Session.UrlVariable}='{url}'");
        Console.Out.WriteLine($"export {Session.TokenVariable}='{token}'");
        return 0;
    }
}
