using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Shows the tenant's recovery certificate for offline exports (ADR 0960): whose it is, its thumbprint and expiry, or
/// that none is set (and so every offline export is refused).
/// </summary>
/// <remarks>Followed from the tenant settings' <c>settings-recovery-certificate</c> rel, which a tenant administrator holds.</remarks>
public sealed class RecoveryCertificateShowSettings : TenantSessionSettings
{
    [CommandOption("--pem")]
    [Description("Print the certificate itself (PEM) on stdout, for a script, instead of describing it.")]
    public bool Pem { get; init; }
}

public sealed class RecoveryCertificateShowCommand(ILogger<RecoveryCertificateShowCommand> logger) : AsyncCommand<RecoveryCertificateShowSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RecoveryCertificateShowSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var (resource, _) = await RecoveryCertificateSurface.ReadAsync(new SimplArchiveApi(http), cancellationToken);
        if (!RecoveryCertificateSurface.Configured(resource))
        {
            logger.LogInformation("No recovery certificate is set: every offline export is refused until one is.");
            return 0;
        }

        if (settings.Pem)
        {
            // Data output (ADR 0906): the certificate alone on stdout, for `alcifra tenant recovery-certificate show --pem > recovery.pem`.
            Console.Out.Write(resource.GetProperty("certificatePem").GetString());
            return 0;
        }

        logger.LogInformation("{Description}", RecoveryCertificateSurface.Describe(resource));
        return 0;
    }
}

public sealed class RecoveryCertificateFileSettings : TenantSessionSettings
{
    [CommandArgument(0, "<certificate.pem>")]
    [Description("The PUBLIC recovery certificate (PEM). Its private key stays offline and never reaches the server.")]
    public required string File { get; init; }

    public override Spectre.Console.ValidationResult Validate() =>
        !System.IO.File.Exists(File) ? Spectre.Console.ValidationResult.Error($"No file at {File}.") : base.Validate();
}

/// <summary>Sets the tenant's recovery certificate where none is set yet (ADR 0960); <c>replace</c> changes one.</summary>
public sealed class RecoveryCertificateSetCommand(ILogger<RecoveryCertificateSetCommand> logger) : AsyncCommand<RecoveryCertificateFileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RecoveryCertificateFileSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var written = await RecoveryCertificateSurface.WriteAsync(new SimplArchiveApi(http), await File.ReadAllTextAsync(settings.File, cancellationToken),
            replacing: false, cancellationToken);
        logger.LogInformation("Recovery certificate set: {Description}", RecoveryCertificateSurface.Describe(written));
        return 0;
    }
}

/// <summary>
/// Replaces the tenant's recovery certificate (ADR 0960). Archives already made stay sealed to the OLD one, so its
/// private key must be kept as long as they are; new archives are sealed to the new one.
/// </summary>
public sealed class RecoveryCertificateReplaceCommand(ILogger<RecoveryCertificateReplaceCommand> logger) : AsyncCommand<RecoveryCertificateFileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RecoveryCertificateFileSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var written = await RecoveryCertificateSurface.WriteAsync(new SimplArchiveApi(http), await File.ReadAllTextAsync(settings.File, cancellationToken),
            replacing: true, cancellationToken);
        logger.LogInformation("Recovery certificate replaced: {Description}. Keep the previous private key: the archives made until now open only with it.",
            RecoveryCertificateSurface.Describe(written));
        return 0;
    }
}

public static class RecoveryCertificateSurface
{
    public const string Rel = "settings-recovery-certificate";

    /// <summary>The resource and its ETag, followed from the root's <c>tenantSettings</c> (ADR 0543).</summary>
    public static async Task<(JsonElement Resource, string? ETag)> ReadAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var settings = await hypermedia.RootHrefAsync("tenantSettings", cancellationToken);
        var href = Hypermedia.Href(await hypermedia.LinksOfAsync(settings, cancellationToken), Rel, "The tenant settings");
        return await api.GetWithETagAsync(href, cancellationToken);
    }

    /// <summary>
    /// Writes the certificate with the read's ETag as <c>If-Match</c> (ADR 0794): <c>set</c> refuses where one is set and
    /// <c>replace</c> where none is, so neither is done by accident.
    /// </summary>
    public static async Task<JsonElement> WriteAsync(SimplArchiveApi api, string certificatePem, bool replacing, CancellationToken cancellationToken)
    {
        var (resource, etag) = await ReadAsync(api, cancellationToken);
        if (Configured(resource) != replacing)
        {
            throw new CliException(replacing
                ? "No recovery certificate is set yet; use 'alcifra tenant recovery-certificate set'."
                : "A recovery certificate is already set; use 'alcifra tenant recovery-certificate replace' to change it.");
        }

        var self = Hypermedia.Href(Hypermedia.LinksOf(resource), "self", "The recovery certificate");
        return await api.PutWithETagAsync(self, new { certificatePem }, etag, cancellationToken);
    }

    public static bool Configured(JsonElement resource) =>
        resource.TryGetProperty("configured", out var configured) && configured.GetBoolean();

    public static string Describe(JsonElement resource) =>
        $"{resource.GetProperty("subject").GetString()}, SHA-256 thumbprint {resource.GetProperty("thumbprint").GetString()}, "
        + $"valid until {resource.GetProperty("notAfter").GetDateTimeOffset():yyyy-MM-dd}";
}
