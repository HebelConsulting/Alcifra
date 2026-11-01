using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>Lists the tenant's registered domains with their roles (ADR 1015), through the root's <c>mailDomains</c> rel.</summary>
public sealed class MailDomainListCommand(ILogger<MailDomainListCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var listing = await MailDomainSurface.ListAsync(new AlcifraApi(http), cancellationToken);
        foreach (var domain in listing.GetProperty("domains").EnumerateArray())
        {
            logger.LogInformation("{Domain}: {Roles}, {Verified}", domain.GetProperty("domain").GetString(),
                MailDomainSurface.Roles(domain.GetProperty("isIdentity").GetBoolean(), domain.GetProperty("isReceiving").GetBoolean()),
                domain.GetProperty("verified").GetBoolean() ? "verified" : "awaiting DNS verification");
        }

        return 0;
    }
}

public sealed class MailDomainAddSettings : TenantSessionSettings
{
    [CommandArgument(0, "<domain>")]
    [Description("The domain, for example example.com.")]
    public string Domain { get; init; } = string.Empty;

    [CommandOption("--identity-only")]
    [Description("Account addresses live on it; no mail is received for it. Another tenant may hold it too.")]
    public bool IdentityOnly { get; init; }

    [CommandOption("--receiving-only")]
    [Description("Mail to it is received and filed; no account addresses or aliases live on it.")]
    public bool ReceivingOnly { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        { IdentityOnly: true, ReceivingOnly: true } => ValidationResult.Error("Name one of --identity-only and --receiving-only, or neither for both roles."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Claims a domain for the tenant with its roles (ADR 1015); it is verified by publishing the DNS record the server
/// hands back (ADR 0692). Through the listing's <c>add</c> rel.
/// </summary>
public sealed class MailDomainAddCommand(ILogger<MailDomainAddCommand> logger) : AsyncCommand<MailDomainAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MailDomainAddSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var listing = await MailDomainSurface.ListAsync(api, cancellationToken);
        var add = Hypermedia.Href(Hypermedia.LinksOf(listing), "add", "The mail-domain listing");
        var created = await api.PostAsync(add,
            new { domain = settings.Domain, isIdentity = !settings.ReceivingOnly, isReceiving = !settings.IdentityOnly }, cancellationToken);

        logger.LogInformation("Claimed {Domain} ({Roles}). Publish TXT {Name} = {Value}, then verify it.",
            created.GetProperty("domain").GetString(),
            MailDomainSurface.Roles(created.GetProperty("isIdentity").GetBoolean(), created.GetProperty("isReceiving").GetBoolean()),
            created.TryGetProperty("challengeName", out var name) ? name.GetString() : null,
            created.TryGetProperty("challengeValue", out var value) ? value.GetString() : null);
        return 0;
    }
}

public sealed class MailDomainRolesSettings : TenantSessionSettings
{
    [CommandArgument(0, "<domain>")]
    [Description("A domain the tenant has registered.")]
    public string Domain { get; init; } = string.Empty;

    [CommandOption("--identity <BOOL>")]
    [Description("true: account addresses and identity aliases live on it.")]
    public required bool Identity { get; init; }

    [CommandOption("--receiving <BOOL>")]
    [Description("true: mail to it is received and filed. Switching it off stops mail arriving.")]
    public required bool Receiving { get; init; }
}

/// <summary>
/// Sets a domain's roles (ADR 1015): read for its ETag, written with it as <c>If-Match</c> through its <c>roles</c> rel.
/// </summary>
public sealed class MailDomainRolesCommand(ILogger<MailDomainRolesCommand> logger) : AsyncCommand<MailDomainRolesSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MailDomainRolesSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var row = MailDomainSurface.Find(await MailDomainSurface.ListAsync(api, cancellationToken), settings.Domain);
        var (domain, etag) = await api.GetWithETagAsync(Hypermedia.Href(Hypermedia.LinksOf(row), "self", settings.Domain), cancellationToken);
        var roles = Hypermedia.Href(Hypermedia.LinksOf(domain), "roles", settings.Domain);
        var written = await api.PutWithETagAsync(roles, new { isIdentity = settings.Identity, isReceiving = settings.Receiving }, etag, cancellationToken);

        logger.LogInformation("{Domain} is now {Roles}.", settings.Domain,
            MailDomainSurface.Roles(written.GetProperty("isIdentity").GetBoolean(), written.GetProperty("isReceiving").GetBoolean()));
        return 0;
    }
}

internal static class MailDomainSurface
{
    internal static async Task<JsonElement> ListAsync(AlcifraApi api, CancellationToken cancellationToken) =>
        await api.GetAsync(await new Hypermedia(api).RootHrefAsync("mailDomains", cancellationToken), cancellationToken);

    // Compared as the server compares: case-insensitive, without the root dot.
    internal static JsonElement Find(JsonElement listing, string domain) =>
        listing.GetProperty("domains").EnumerateArray()
            .FirstOrDefault(d => string.Equals(d.GetProperty("domain").GetString()?.TrimEnd('.'), domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } row
            ? row
            : throw new CliException($"'{domain}' is not registered for this tenant.");

    internal static string Roles(bool isIdentity, bool isReceiving) => (isIdentity, isReceiving) switch
    {
        (true, true) => "identity and receiving",
        (true, false) => "identity only",
        _ => "receiving only",
    };
}
