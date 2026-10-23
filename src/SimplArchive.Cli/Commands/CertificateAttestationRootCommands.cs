using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// A tenant's selection of the installation's attestation roots (ADR 0963): list what the installation trusts and which
/// of those this tenant has switched off; switch one off or back on. A tenant may narrow the installation's trust, never
/// widen it. Followed from the root's <c>certificateAttestationRoots</c> rel, present while a module licensing enrolment
/// is active; a module administrator's, like the certificate settings.
/// </summary>
public sealed class CertificateAttestationRootListCommand(ILogger<CertificateAttestationRootListCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var (selection, _) = await TenantAttestationRootSurface.ReadAsync(new SimplArchiveApi(http), cancellationToken);
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in new[] { "name", "here", "thumbprint", "id" })
        {
            table.AddColumn(column);
        }

        foreach (var root in TenantAttestationRootSurface.Roots(selection))
        {
            table.AddRow(
                Markup.Escape(root.GetProperty("name").GetString() ?? string.Empty),
                root.GetProperty("trusted").GetBoolean() ? "[green]trusted[/]"
                    : root.GetProperty("enabledForInstallation").GetBoolean() ? "[yellow]switched off here[/]" : "off for the installation",
                Markup.Escape(root.GetProperty("thumbprint").GetString()![..16]),
                root.GetProperty("id").GetString()!);
        }

        logger.LogInformation("{Table}", CliLogging.Render(table));
        return 0;
    }
}

public sealed class CertificateAttestationRootSelectSettings : TenantSessionSettings
{
    [CommandArgument(0, "<root>")]
    [Description("The root: its id, name, or the start of its thumbprint.")]
    public required string Root { get; init; }
}

/// <summary>Switches a root off for this tenant: keys attested to it stop addressing their holders here, at once.</summary>
public sealed class CertificateAttestationRootDisableCommand(ILogger<CertificateAttestationRootDisableCommand> logger)
    : AsyncCommand<CertificateAttestationRootSelectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CertificateAttestationRootSelectSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var written = await TenantAttestationRootSurface.SwitchAsync(new SimplArchiveApi(http), settings.Root, off: true, cancellationToken);
        logger.LogInformation("Switched off for this tenant; {Count} root(s) are now off here.", TenantAttestationRootSurface.Disabled(written).Count);
        return 0;
    }
}

/// <summary>Switches a root back on for this tenant (it is trusted here again where the installation trusts it).</summary>
public sealed class CertificateAttestationRootEnableCommand(ILogger<CertificateAttestationRootEnableCommand> logger)
    : AsyncCommand<CertificateAttestationRootSelectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CertificateAttestationRootSelectSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var written = await TenantAttestationRootSurface.SwitchAsync(new SimplArchiveApi(http), settings.Root, off: false, cancellationToken);
        logger.LogInformation("Switched back on for this tenant; {Count} root(s) are still off here.", TenantAttestationRootSurface.Disabled(written).Count);
        return 0;
    }
}

/// <summary>The rel-following behind the commands, one place, testable against a live installation.</summary>
public static class TenantAttestationRootSurface
{
    public const string Rel = "certificateAttestationRoots";

    public static async Task<(JsonElement Selection, string? ETag)> ReadAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        string href;
        try
        {
            href = await new Hypermedia(api).RootHrefAsync(Rel, cancellationToken);
        }
        catch (CliException)
        {
            throw new CliException("This installation does not offer certificate enrolment for this tenant: no active module licenses it.");
        }

        return await api.GetWithETagAsync(href, cancellationToken);
    }

    public static IEnumerable<JsonElement> Roots(JsonElement selection) => selection.GetProperty("roots").EnumerateArray();

    public static IReadOnlyList<Guid> Disabled(JsonElement selection) =>
        [.. Roots(selection).Where(r => r.GetProperty("disabledHere").GetBoolean()).Select(r => r.GetProperty("id").GetGuid())];

    /// <summary>Reads the selection, changes one root, and PUTs the WHOLE list back with the tenant's tag.</summary>
    public static async Task<JsonElement> SwitchAsync(SimplArchiveApi api, string selector, bool off, CancellationToken cancellationToken)
    {
        var (selection, etag) = await ReadAsync(api, cancellationToken);
        var matches = Roots(selection).Where(r =>
            string.Equals(r.GetProperty("id").GetString(), selector, StringComparison.OrdinalIgnoreCase)
            || r.GetProperty("name").GetString() == selector
            || (selector.Length >= 8 && r.GetProperty("thumbprint").GetString()!.StartsWith(selector, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count != 1)
        {
            throw new CliException(matches.Count == 0
                ? $"No attestation root matches '{selector}'. See saconsole certificates attestation-roots list."
                : $"'{selector}' matches {matches.Count} attestation roots; name it by id.");
        }

        var id = matches[0].GetProperty("id").GetGuid();
        var disabled = Disabled(selection).Where(d => d != id).ToList();
        if (off)
        {
            disabled.Add(id);
        }

        var self = Hypermedia.Href(Hypermedia.LinksOf(selection), "self", "The attestation-root selection");
        return await api.PutWithETagAsync(self, new { disabledRootIds = disabled }, etag, cancellationToken);
    }
}
