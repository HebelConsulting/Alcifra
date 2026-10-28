using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// The installation's trusted attestation roots (ADR 0963), a PLATFORM ADMINISTRATOR's surface: which hardware the
/// installation accepts as attested. Followed from the root's <c>attestationRoots</c> rel, emitted only to a platform
/// administrator.
/// </summary>
public sealed class AttestationRootListCommand(ILogger<AttestationRootListCommand> logger) : AsyncCommand<PlatformAdminSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PlatformAdminSettings settings, CancellationToken cancellationToken)
    {
        using var http = await AttestationRootSurface.ClientAsync(settings, cancellationToken);
        var listing = await AttestationRootSurface.ListAsync(new SimplArchiveApi(http), cancellationToken);
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in new[] { "name", "trusted", "catalogue", "thumbprint", "id" })
        {
            table.AddColumn(column);
        }

        foreach (var root in AttestationRootSurface.Roots(listing))
        {
            table.AddRow(
                Markup.Escape(root.GetProperty("name").GetString() ?? string.Empty),
                root.GetProperty("enabled").GetBoolean() ? "[green]yes[/]" : "no",
                Markup.Escape(root.TryGetProperty("catalogueKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : "uploaded"),
                Markup.Escape(root.GetProperty("thumbprint").GetString()![..16]),
                root.GetProperty("id").GetString()!);
        }

        logger.LogInformation("{Table}", CliLogging.Render(table));
        return 0;
    }
}

public sealed class AttestationRootAddSettings : PlatformAdminSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The root's PUBLIC certificate, PEM or DER, as its manufacturer publishes it.")]
    public required string File { get; init; }

    [CommandOption("--name <NAME>")]
    [Description("What to call it; the certificate's common name by default.")]
    public string? Name { get; init; }

    [CommandOption("--enable")]
    [Description("Trust it at once. Without this it is added switched off.")]
    public bool Enable { get; init; }
}

public sealed class AttestationRootAddCommand(ILogger<AttestationRootAddCommand> logger) : AsyncCommand<AttestationRootAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AttestationRootAddSettings settings, CancellationToken cancellationToken)
    {
        var pem = AttestationFiles.Pem(settings.File);   // PEM or DER, never a private key
        using var http = await AttestationRootSurface.ClientAsync(settings, cancellationToken);
        var added = await AttestationRootSurface.AddAsync(new SimplArchiveApi(http), pem, settings.Name, settings.Enable, cancellationToken);
        logger.LogInformation("Added attestation root {Name} ({State}), id {Id}.", added.GetProperty("name").GetString(),
            added.GetProperty("enabled").GetBoolean() ? "trusted" : "switched off", added.GetProperty("id").GetString());
        return 0;
    }
}

public class AttestationRootSelectSettings : PlatformAdminSettings
{
    [CommandArgument(0, "<root>")]
    [Description("The root: its id, catalogue key, name, or the start of its thumbprint.")]
    public required string Root { get; init; }
}

/// <summary>Switches a root on for the installation.</summary>
public sealed class AttestationRootEnableCommand(ILogger<AttestationRootEnableCommand> logger) : AsyncCommand<AttestationRootSelectSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, AttestationRootSelectSettings settings, CancellationToken cancellationToken) =>
        AttestationRootSurface.ChangeAndReportAsync(settings, settings.Root, (name, _) => (name, true), logger, cancellationToken);
}

/// <summary>Switches a root off for the installation: every key attested to it stops addressing its holder, at once.</summary>
public sealed class AttestationRootDisableCommand(ILogger<AttestationRootDisableCommand> logger) : AsyncCommand<AttestationRootSelectSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, AttestationRootSelectSettings settings, CancellationToken cancellationToken) =>
        AttestationRootSurface.ChangeAndReportAsync(settings, settings.Root, (name, _) => (name, false), logger, cancellationToken);
}

public sealed class AttestationRootRenameSettings : AttestationRootSelectSettings
{
    [CommandArgument(1, "<name>")]
    [Description("The new name.")]
    public required string Name { get; init; }
}

public sealed class AttestationRootRenameCommand(ILogger<AttestationRootRenameCommand> logger) : AsyncCommand<AttestationRootRenameSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, AttestationRootRenameSettings settings, CancellationToken cancellationToken) =>
        AttestationRootSurface.ChangeAndReportAsync(settings, settings.Root, (_, enabled) => (settings.Name, enabled), logger, cancellationToken);
}

/// <summary>The rel-following behind the commands, one place, testable against a live installation.</summary>
public static class AttestationRootSurface
{
    public const string Rel = "attestationRoots";

    internal static async Task<HttpClient> ClientAsync(PlatformAdminSettings settings, CancellationToken cancellationToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        await new SimplArchiveApi(http).AuthenticateAsPlatformAdministratorAsync(settings.ClientId, settings.ResolvedSecret, cancellationToken);
        return http;
    }

    public static async Task<JsonElement> ListAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        string href;
        try
        {
            href = await new Hypermedia(api).RootHrefAsync(Rel, cancellationToken);
        }
        catch (CliException)
        {
            throw new CliException("This installation does not offer its attestation roots to this principal: sign in as a platform administrator.");
        }

        return await api.GetAsync(href, cancellationToken);
    }

    public static IEnumerable<JsonElement> Roots(JsonElement listing) => listing.GetProperty("roots").EnumerateArray();

    public static async Task<JsonElement> AddAsync(SimplArchiveApi api, string pem, string? name, bool enabled, CancellationToken cancellationToken)
    {
        var listing = await ListAsync(api, cancellationToken);
        return await api.PostAsync(Hypermedia.Href(Hypermedia.LinksOf(listing), "upload", "The attestation roots"),
            new { certificatePem = pem, name, enabled }, cancellationToken);
    }

    /// <summary>Reads the root for its tag, then writes its whole value back changed by <paramref name="change"/>.</summary>
    public static async Task<JsonElement> ChangeAsync(
        SimplArchiveApi api, string selector, Func<string, bool, (string Name, bool Enabled)> change, CancellationToken cancellationToken)
    {
        var root = Find(await ListAsync(api, cancellationToken), selector);
        var self = Hypermedia.Href(Hypermedia.LinksOf(root), "self", "The attestation root");
        var (current, etag) = await api.GetWithETagAsync(self, cancellationToken);
        var (name, enabled) = change(current.GetProperty("name").GetString() ?? string.Empty, current.GetProperty("enabled").GetBoolean());
        return await api.PutWithETagAsync(self, new { name, enabled }, etag, cancellationToken);
    }

    internal static async Task<int> ChangeAndReportAsync(
        AttestationRootSelectSettings settings, string selector, Func<string, bool, (string, bool)> change, ILogger logger, CancellationToken cancellationToken)
    {
        using var http = await ClientAsync(settings, cancellationToken);
        var written = await ChangeAsync(new SimplArchiveApi(http), selector, change, cancellationToken);
        logger.LogInformation("Attestation root {Name}: {State}.", written.GetProperty("name").GetString(),
            written.GetProperty("enabled").GetBoolean() ? "trusted" : "switched off");
        return 0;
    }

    /// <summary>The one root a selector names: id, catalogue key, exact name, or a thumbprint prefix of at least eight.</summary>
    public static JsonElement Find(JsonElement listing, string selector)
    {
        var matches = Roots(listing).Where(r =>
            string.Equals(r.GetProperty("id").GetString(), selector, StringComparison.OrdinalIgnoreCase)
            || (r.TryGetProperty("catalogueKey", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() == selector)
            || r.GetProperty("name").GetString() == selector
            || (selector.Length >= 8 && r.GetProperty("thumbprint").GetString()!.StartsWith(selector, StringComparison.OrdinalIgnoreCase))).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No attestation root matches '{selector}'. See alcifra attestation-roots list."),
            _ => throw new CliException($"'{selector}' matches {matches.Count} attestation roots; name it by id."),
        };
    }
}
