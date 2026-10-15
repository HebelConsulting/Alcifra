using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Shows the tenant's certificate enrolment settings (ADR 0936): whether users enrol their own reader certificate, and
/// where the "Reader certificates" folder is created.
/// </summary>
/// <remarks>
/// Followed from the root's <c>certificateSettings</c> rel, present while a module licensing enrolment is active; a
/// module administrator may read it, a tenant administrator or a service account granted <c>CanManageModules</c>.
/// </remarks>
public sealed class CertificateSettingsShowCommand(ILogger<CertificateSettingsShowCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var (resource, _) = await CertificateSettingsSurface.ReadAsync(new SimplArchiveApi(http), cancellationToken);
        var (selfService, parent) = CertificateSettingsSurface.Current(resource);
        logger.LogInformation("Self-service enrolment: {SelfService}. Reader-certificate folder parent: {Parent}.",
            selfService ? "on" : "off", parent?.ToString() ?? "not set");
        return 0;
    }
}

public sealed class CertificateSettingsSetSettings : TenantSessionSettings
{
    [CommandOption("--self-service <on|off>")]
    [Description("Whether users may enrol their own reader certificate.")]
    public string? SelfService { get; init; }

    [CommandOption("--parent <folder-id>")]
    [Description("The repository or folder the \"Reader certificates\" folder is created under; its rights decide who sees whose certificate.")]
    public Guid? Parent { get; init; }

    [CommandOption("--no-parent")]
    [Description("Clear the parent.")]
    public bool NoParent { get; init; }

    public override Spectre.Console.ValidationResult Validate() =>
        SelfService is not (null or "on" or "off")
            ? Spectre.Console.ValidationResult.Error("--self-service takes on or off.")
            : Parent is not null && NoParent
                ? Spectre.Console.ValidationResult.Error("--parent and --no-parent exclude each other.")
                : base.Validate();
}

/// <summary>
/// Changes the tenant's certificate enrolment settings (ADR 0936). What is not named keeps its current value.
/// </summary>
/// <remarks>
/// Reads the resource first for its ETag, then writes the whole value back to the same address with that ETag as
/// <c>If-Match</c>: a colleague's change in between is refused rather than overwritten.
/// </remarks>
public sealed class CertificateSettingsSetCommand(ILogger<CertificateSettingsSetCommand> logger) : AsyncCommand<CertificateSettingsSetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateSettingsSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var (resource, etag) = await CertificateSettingsSurface.ReadAsync(api, cancellationToken);
        var (selfService, parent) = CertificateSettingsSurface.Current(resource);

        var target = Hypermedia.Href(Hypermedia.LinksOf(resource), "self", "The certificate settings");
        var written = await api.PutWithETagAsync(target, new
        {
            selfServiceEnabled = settings.SelfService is { } value ? value == "on" : selfService,
            parentFolderId = settings.NoParent ? null : settings.Parent ?? parent,
        }, etag, cancellationToken);

        var (nowSelfService, nowParent) = CertificateSettingsSurface.Current(written);
        logger.LogInformation("Self-service enrolment: {SelfService}. Reader-certificate folder parent: {Parent}.",
            nowSelfService ? "on" : "off", nowParent?.ToString() ?? "not set");
        return 0;
    }
}

internal static class CertificateSettingsSurface
{
    internal const string Rel = "certificateSettings";

    internal static async Task<(JsonElement Resource, string? ETag)> ReadAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        string href;
        try
        {
            href = await new Hypermedia(api).RootHrefAsync(Rel, cancellationToken);
        }
        catch (CliException)
        {
            throw new CliException(
                "This installation does not offer certificate enrolment for this tenant: no active module licenses it.");
        }

        return await api.GetWithETagAsync(href, cancellationToken);
    }

    internal static (bool SelfService, Guid? Parent) Current(JsonElement resource) => (
        resource.TryGetProperty("selfServiceEnabled", out var on) && on.ValueKind == JsonValueKind.True,
        resource.TryGetProperty("parentFolderId", out var parent) && parent.ValueKind == JsonValueKind.String
            ? parent.GetGuid()
            : null);
}
