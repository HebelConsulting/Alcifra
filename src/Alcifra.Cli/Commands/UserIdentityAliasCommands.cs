using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public class UserAliasSettings : TenantSessionSettings
{
    [CommandOption("--email <EMAIL>")]
    [Description("The user, by the address they sign in with.")]
    public string Email { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Email) => ValidationResult.Error("Name the user with --email <EMAIL>."),
        _ => ValidationResult.Success(),
    };
}

public sealed class UserAliasAddressSettings : UserAliasSettings
{
    [CommandArgument(0, "<address>")]
    [Description("The alias: an address on one of the tenant's verified identity domains.")]
    public string Address { get; init; } = string.Empty;
}

/// <summary>Lists a user's identity aliases (ADR 1015), through the user's <c>identity-aliases</c> rel.</summary>
public sealed class UserAliasListCommand(ILogger<UserAliasListCommand> logger) : AsyncCommand<UserAliasSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserAliasSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var aliases = await api.GetAsync(await UserAliasSurface.AliasesHrefAsync(api, settings.Email, cancellationToken), cancellationToken);
        foreach (var alias in aliases.GetProperty("aliases").EnumerateArray())
        {
            logger.LogInformation("{Address}", alias.GetProperty("address").GetString());
        }

        return 0;
    }
}

/// <summary>
/// Adds an identity alias to a user (ADR 1015): confirmed at once, since the administrator vouches for it. The alias
/// does not sign in; Strict certificate binding accepts a certificate naming it.
/// </summary>
public sealed class UserAliasAddCommand(ILogger<UserAliasAddCommand> logger) : AsyncCommand<UserAliasAddressSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserAliasAddressSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var aliases = await api.GetAsync(await UserAliasSurface.AliasesHrefAsync(api, settings.Email, cancellationToken), cancellationToken);
        if (UserAliasSurface.Find(aliases, settings.Address) is not null)
        {
            logger.LogInformation("{Address} is already an identity alias of {Email}.", settings.Address, settings.Email);
            return 0;
        }

        await api.PostAsync(Hypermedia.Href(Hypermedia.LinksOf(aliases), "add", "The identity aliases"), new { address = settings.Address }, cancellationToken);
        logger.LogInformation("Added the identity alias {Address} to {Email}.", settings.Address, settings.Email);
        return 0;
    }
}

/// <summary>Removes an identity alias, with the alias's ETag as <c>If-Match</c>.</summary>
public sealed class UserAliasRemoveCommand(ILogger<UserAliasRemoveCommand> logger) : AsyncCommand<UserAliasAddressSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserAliasAddressSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var aliases = await api.GetAsync(await UserAliasSurface.AliasesHrefAsync(api, settings.Email, cancellationToken), cancellationToken);
        if (UserAliasSurface.Find(aliases, settings.Address) is not { } alias)
        {
            logger.LogInformation("{Address} is not an identity alias of {Email}; nothing to remove.", settings.Address, settings.Email);
            return 0;
        }

        await api.DeleteWithETagAsync(Hypermedia.Href(Hypermedia.LinksOf(alias), "remove", settings.Address),
            alias.GetProperty("eTag").GetString(), cancellationToken);
        logger.LogInformation("Removed the identity alias {Address} from {Email}.", settings.Address, settings.Email);
        return 0;
    }
}

internal static class UserAliasSurface
{
    // The user's row from the root's users listing, then its identity-aliases rel: one read of the listing, one follow.
    internal static async Task<string> AliasesHrefAsync(AlcifraApi api, string email, CancellationToken cancellationToken)
    {
        for (string? page = await new Hypermedia(api).RootHrefAsync("users", cancellationToken); page is not null;)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            foreach (var user in listing.GetProperty("users").EnumerateArray())
            {
                if (string.Equals(user.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
                {
                    return Hypermedia.Href(Hypermedia.LinksOf(user), "identity-aliases", email);
                }
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        throw new CliException($"No user signs in as '{email}' in this tenant.");
    }

    internal static JsonElement? Find(JsonElement aliases, string address) =>
        aliases.GetProperty("aliases").EnumerateArray()
            .Where(a => string.Equals(a.GetProperty("address").GetString(), address.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(a => (JsonElement?)a)
            .FirstOrDefault();
}
