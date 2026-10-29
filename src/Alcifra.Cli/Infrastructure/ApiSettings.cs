using System.ComponentModel;
using Alcifra.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Infrastructure;

/// <summary>Where the installation is. Every command needs this and nothing else by default.</summary>
/// <remarks>
/// <c>--url</c> falls back to <see cref="UrlVariable"/>, which <c>alcifra login</c> prints as a shell
/// export. That is the whole of the "current installation" idea: it lives in the environment beside the
/// token, so there is exactly ONE place to look and no stored context that can point a command at an
/// installation the administrator has forgotten they selected. An explicit <c>--url</c> always wins, so a
/// one-off against another installation needs no unsetting.
/// </remarks>
public class ApiSettings : CommandSettings
{
    /// <summary>Environment variable carrying the installation URL (set by <c>alcifra login</c>, and by <c>apass login</c>).</summary>
    /// <remarks>
    /// One session for both tools (owner, 2026-10-25): it belongs to the installation, not to one tool. The names
    /// before the rename are read after it until <see cref="WireNames.Legacy"/> retires (v0.56.0).
    /// </remarks>
    public const string UrlVariable = WireNames.UrlVariable;

    /// <summary>Environment variable carrying the user access token (set by <c>alcifra login</c>).</summary>
    public const string TokenVariable = WireNames.TokenVariable;

    [CommandOption("--url <URL>")]
    [Description("Base URL of the installation, e.g. https://archive.example.com. Defaults to $ALCIFRA_URL.")]
    public string? Url { get; init; }

    /// <summary>The installation to act on — the option if given, else the environment (current name first).</summary>
    public string ResolvedUrl => !string.IsNullOrWhiteSpace(Url)
        ? Url
        : First(UrlVariable, WireNames.Legacy.SessionUrlVariable, WireNames.Legacy.CliUrlVariable);

    /// <summary>A client secret from the environment — current name first — or the empty string; never an option's value.</summary>
    public static string ClientSecretFromEnvironment =>
        First(WireNames.ClientSecretVariable, WireNames.Legacy.CliClientSecretVariable);

    /// <summary>The first of these environment variables that is set, or the empty string.</summary>
    protected static string First(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    // Spectre builds settings by reflection and does not enforce `required` — validate explicitly.
    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(ResolvedUrl)
        ? ValidationResult.Error($"No installation. Pass --url, or run 'alcifra login --url …' and eval its output to set ${UrlVariable}.")
        : ValidationResult.Success();
}

/// <summary>
/// Settings for a command acting INSIDE A TENANT — the session <c>alcifra login</c> produced: a user through
/// the device flow, or a tenant service account through <c>--client-id</c> (ADR 0870). Either way the
/// commands follow the same rels, and the server answers what that principal may do.
/// </summary>
/// <remarks>
/// A distinct type for the same reason <see cref="PlatformAdminSettings"/> is one (ADR 0822): which principal
/// a command acts as must be answerable by READING it. Three principals now appear in this one binary, and a
/// command that silently reached for whichever credential happened to be in the environment would be an
/// escalation surprise rather than a failure.
/// </remarks>
public class TenantSessionSettings : ApiSettings
{
    /// <summary>The access token from the environment — never an option, so it cannot land in shell history.</summary>
    public string ResolvedToken => First(TokenVariable, WireNames.Legacy.SessionTokenVariable, WireNames.Legacy.CliTokenVariable);

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrEmpty(ResolvedToken) => ValidationResult.Error(
            $"Not signed in. Run: eval \"$(alcifra login --url {ResolvedUrl})\""),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Settings for the acts only a PLATFORM ADMINISTRATOR may perform — creating a tenant, above all.
/// </summary>
/// <remarks>
/// Deliberately a distinct type rather than a flag on <see cref="ApiSettings"/>. ADR 0822 requires the two
/// principals to be visibly different at the call site: a command that quietly picked the wrong one would be
/// an escalation surprise rather than a failure, and the type is what makes "which principal is this?"
/// answerable by reading the command rather than by running it.
/// </remarks>
public class PlatformAdminSettings : ApiSettings
{
    [CommandOption("--client-id <ID>")]
    [Description("Platform-administrator client id (client-credentials grant).")]
    public required string ClientId { get; init; }

    [CommandOption("--client-secret <SECRET>")]
    [Description("Platform-administrator client secret. Prefer ALCIFRA_CLIENT_SECRET in the environment.")]
    public string? ClientSecret { get; init; }

    /// <summary>The secret, from the option or the environment — never echoed.</summary>
    public string ResolvedSecret => string.IsNullOrEmpty(ClientSecret)
        ? ClientSecretFromEnvironment
        : ClientSecret;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        { ClientId: null or "" } => ValidationResult.Error("Missing required option --client-id."),
        _ when string.IsNullOrEmpty(ResolvedSecret) => ValidationResult.Error(
            $"No client secret. Pass --client-secret, or set {WireNames.ClientSecretVariable} so it does not land in shell history."),
        _ => ValidationResult.Success(),
    };
}
