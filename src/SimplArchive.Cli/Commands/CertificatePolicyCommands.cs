using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// Shows the tenant's certificate policy (ADR 0936): what a certificate must meet before it is enrolled.
/// </summary>
/// <remarks>
/// Followed from the root's <c>certificatePolicy</c> rel, present while a module licensing enrolment is active. Anyone
/// may read it, because a refused enrollee needs to see what is wanted.
/// </remarks>
public sealed class CertificatePolicyShowCommand(ILogger<CertificatePolicyShowCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var (_, resource, _) = await CertificatePolicySurface.ReadAsync(new SimplArchiveApi(http), cancellationToken);
        CertificatePolicySurface.Report(logger, resource);
        return 0;
    }
}

public sealed class CertificatePolicySetSettings : TenantSessionSettings
{
    [CommandOption("--san-must-match-account <on|off>")]
    [Description("Whether a certificate's e-mail address must be its holder's (ADR 0971). On by default; switch it off only for an authority that issues certificates without e-mail addresses.")]
    public string? SanMustMatchAccount { get; init; }

    public override Spectre.Console.ValidationResult Validate() =>
        SanMustMatchAccount switch
        {
            null => Spectre.Console.ValidationResult.Error("Name what to change: --san-must-match-account on|off."),
            "on" or "off" => base.Validate(),
            _ => Spectre.Console.ValidationResult.Error("--san-must-match-account takes on or off."),
        };
}

/// <summary>
/// Changes the tenant's certificate policy (ADR 0971). What is not named keeps its current value.
/// </summary>
/// <remarks>
/// Reads the policy for its ETag and writes the WHOLE policy back with that ETag as <c>If-Match</c> — every rule the
/// administrator did not name travels unchanged, and a colleague's change in between is refused rather than overwritten.
/// The rule takes effect at the next enrolment; nothing already enrolled is re-checked.
/// </remarks>
public sealed class CertificatePolicySetCommand(ILogger<CertificatePolicySetCommand> logger) : AsyncCommand<CertificatePolicySetSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificatePolicySetSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var written = await CertificatePolicySurface.SetSanMustMatchAccountAsync(
            new SimplArchiveApi(http), settings.SanMustMatchAccount == "on", cancellationToken);
        CertificatePolicySurface.Report(logger, written);
        return 0;
    }
}

public static class CertificatePolicySurface
{
    public const string Rel = "certificatePolicy";

    public const string LegacyRel = "encryption:certificate-policy";

    /// <summary>The policy's address, the policy, and its ETag.</summary>
    public static async Task<(string Href, JsonElement Resource, string? ETag)> ReadAsync(SimplArchiveApi api, CancellationToken cancellationToken)
    {
        var href = await AddressAsync(new Hypermedia(api), cancellationToken);
        var (resource, etag) = await api.GetWithETagAsync(href, cancellationToken);
        return (href, resource, etag);
    }

    /// <summary>Sets the holder's-address rule, keeping every other rule as read; returns the policy as written.</summary>
    public static async Task<JsonElement> SetSanMustMatchAccountAsync(SimplArchiveApi api, bool on, CancellationToken cancellationToken)
    {
        var (href, resource, etag) = await ReadAsync(api, cancellationToken);
        var policy = JsonNode.Parse(resource.GetRawText())?.AsObject()
            ?? throw new CliException("The certificate policy could not be read back.");
        policy["sanMustMatchAccount"] = on;
        return await api.PutWithETagAsync(href, policy, etag, cancellationToken);
    }

    public static void Report(ILogger logger, JsonElement policy)
    {
        logger.LogInformation(
            "Holder's e-mail address required: {SanMustMatchAccount}. Issuer pattern: {Issuer}. Common-name pattern: {CommonName}. "
            + "RSA minimum: {RsaMinimum} bits. Curves: {Curves}. Required extended key usages: {Ekus}. Own trust anchor: {Anchor}. "
            + "Further rules: {Rules}.",
            Flag(policy, "sanMustMatchAccount") ? "yes" : "no (opted out)",
            Text(policy, "issuerDnPattern") ?? "any",
            Text(policy, "subjectCnPattern") ?? "any",
            policy.TryGetProperty("minimumKeySizeBits", out var bits) && bits.ValueKind == JsonValueKind.Number
                ? bits.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{Number(policy, "defaultMinimumRsaKeySizeBits")} (default)",
            List(policy, "permittedCurves") is { Length: > 0 } curves ? curves : $"{List(policy, "defaultPermittedCurves")} (default)",
            List(policy, "requiredExtendedKeyUsages") is { Length: > 0 } ekus ? ekus : "none",
            Text(policy, "trustAnchorPem") is null ? "none" : "set",
            policy.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array ? rules.GetArrayLength() : 0);
    }

    // The core's rel since enrolment moved into the core (ADR 0936); the Encryption Module's name is followed when an
    // installation still advertises only that, as CertificateSurface does.
    private static async Task<string> AddressAsync(Hypermedia hypermedia, CancellationToken cancellationToken)
    {
        foreach (var rel in new[] { Rel, LegacyRel })
        {
            try
            {
                return await hypermedia.RootHrefAsync(rel, cancellationToken);
            }
            catch (CliException)
            {
                // not advertised under this name; try the next
            }
        }

        throw new CliException(
            "This installation does not offer certificate enrolment for this tenant: no active module licenses it.");
    }

    private static bool Flag(JsonElement resource, string name) =>
        resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement resource, string name) =>
        resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string Number(JsonElement resource, string name) =>
        resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";

    private static string List(JsonElement resource, string name) =>
        resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray().Select(v => v.GetString()))
            : string.Empty;
}
