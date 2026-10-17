using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class UserCreateSettings : TenantSessionSettings
{
    [CommandOption("--email <EMAIL>")]
    [Description("The user's e-mail, which is also their sign-in name. An existing user with it is reused, not duplicated.")]
    public string Email { get; init; } = string.Empty;

    [CommandOption("--name <NAME>")]
    [Description("The user's display name.")]
    public string Name { get; init; } = string.Empty;

    [CommandOption("--password-stdin")]
    [Description("Read the initial password from stdin. Never an option: it would land in the shell's history. Without it the user has no password and signs in another way.")]
    public bool PasswordFromStdin { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Email) => ValidationResult.Error("Name the user with --email <EMAIL>."),
        _ when string.IsNullOrWhiteSpace(Name) => ValidationResult.Error("Give the user a --name <NAME>."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Creates a user of the signed-in tenant, or finds the one with that e-mail — and prints their id on stdout. Through
/// the root's <c>users</c> rel (GET lists, POST creates: one rel, the method says which — ADR 0719), never a composed
/// address (ADR 0543). Needs the right to manage users.
/// </summary>
public sealed class UserCreateCommand(ILogger<UserCreateCommand> logger) : AsyncCommand<UserCreateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserCreateSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new SimplArchiveApi(http);
        var users = await new Hypermedia(api).RootHrefAsync("users", cancellationToken);

        var id = await FindAsync(api, users, settings.Email, cancellationToken);
        if (id is not null)
        {
            logger.LogInformation("User {Email} already exists.", settings.Email);
        }
        else
        {
            var password = settings.PasswordFromStdin ? (await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n') : null;
            var created = await api.PostAsync(users, new { email = settings.Email, displayName = settings.Name, password }, cancellationToken);
            id = created.GetProperty("id").GetString();
            logger.LogInformation("Created user {Email}.", settings.Email);
        }

        // Data output (ADR 0906): the id alone on stdout, for `id=$(saconsole user create …)`.
        Console.Out.WriteLine(id);
        return 0;
    }

    // Through every page of the listing: e-mails are unique per tenant, compared as the server does, ignoring case.
    private static async Task<string?> FindAsync(SimplArchiveApi api, string href, string email, CancellationToken cancellationToken)
    {
        for (string? page = href; page is not null;)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var user in listing.GetProperty("users").EnumerateArray())
            {
                if (string.Equals(user.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
                {
                    return user.GetProperty("id").GetString();
                }
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        return null;
    }
}
