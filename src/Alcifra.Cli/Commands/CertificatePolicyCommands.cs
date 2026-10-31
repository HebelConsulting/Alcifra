using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

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
        var (_, resource, _) = await CertificatePolicySurface.ReadAsync(new AlcifraApi(http), cancellationToken);
        CertificatePolicySurface.Report(logger, resource);
        return 0;
    }
}

public sealed class CertificatePolicySetSettings : TenantSessionSettings
{
    [CommandOption("--binding <strict|possession>")]
    [Description("How a certificate is tied to its holder (ADR 1017). strict (the default): it carries the holder's address or one of their identity aliases. possession: an administrator enrols the key for its holder, who cannot enrol one themselves until they can prove holding it.")]
    public string? Binding { get; init; }

    [CommandOption("--san-must-match-account <on|off>")]
    [Description("The earlier spelling of --binding: on is strict, off is possession.")]
    public string? SanMustMatchAccount { get; init; }

    // The binding asked for, from either spelling; null when none was named or it was misspelt.
    public string? Chosen => (Binding?.ToLowerInvariant(), SanMustMatchAccount) switch
    {
        ("strict" or "possession", null) => Binding!.ToLowerInvariant(),
        (null, "on") => "strict",
        (null, "off") => "possession",
        _ => null,
    };

    [CommandOption("--trust-anchors <file|none>")]
    [Description("The certificate authorities a certificate must chain to: a PEM file with one certificate or several, any of which suffices (ADR 1017). none removes the rule.")]
    public string? TrustAnchors { get; init; }

    public override Spectre.Console.ValidationResult Validate() => (Binding, SanMustMatchAccount, TrustAnchors) switch
    {
        (null, null, null) => Spectre.Console.ValidationResult.Error("Name what to change: --binding strict|possession, --trust-anchors <file>|none."),
        ({ }, { }, _) => Spectre.Console.ValidationResult.Error("Name the binding once: --binding, or its earlier spelling --san-must-match-account."),
        _ when (Binding is not null || SanMustMatchAccount is not null) && Chosen is null =>
            Spectre.Console.ValidationResult.Error("--binding takes strict or possession; --san-must-match-account takes on or off."),
        (_, _, { } file) when file != "none" && !File.Exists(file) => Spectre.Console.ValidationResult.Error($"No file '{file}' to read the trust anchors from."),
        _ => base.Validate(),
    };

    // The anchors to set: the file's PEM, an empty string for none, or null when not named.
    public string? AnchorsPem => TrustAnchors switch
    {
        null => null,
        "none" => string.Empty,
        var file => File.ReadAllText(file),
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
        var written = await CertificatePolicySurface.SetAsync(new AlcifraApi(http), settings.Chosen, settings.AnchorsPem, cancellationToken);
        CertificatePolicySurface.Report(logger, written);
        return 0;
    }
}

public static class CertificatePolicySurface
{
    public const string Rel = "certificatePolicy";

    public const string LegacyRel = "encryption:certificate-policy";

    /// <summary>The policy's address, the policy, and its ETag.</summary>
    public static async Task<(string Href, JsonElement Resource, string? ETag)> ReadAsync(AlcifraApi api, CancellationToken cancellationToken)
    {
        var href = await AddressAsync(new Hypermedia(api), cancellationToken);
        var (resource, etag) = await api.GetWithETagAsync(href, cancellationToken);
        return (href, resource, etag);
    }

    /// <summary>Sets the binding (<c>strict</c> or <c>possession</c>), keeping every other rule as read; returns the policy
    /// as written. Both fields are written, so a server that knows only the address rule reads the same choice.</summary>
    public static Task<JsonElement> SetBindingAsync(AlcifraApi api, string binding, CancellationToken cancellationToken) =>
        SetAsync(api, binding, anchorsPem: null, cancellationToken);

    /// <summary>
    /// Sets what was named, keeping every other rule as read, in ONE write under the tag of the read: the binding, and the
    /// trust anchors (a PEM with one certificate or several; empty removes them). Returns the policy as written.
    /// </summary>
    public static async Task<JsonElement> SetAsync(AlcifraApi api, string? binding, string? anchorsPem, CancellationToken cancellationToken)
    {
        var (href, resource, etag) = await ReadAsync(api, cancellationToken);
        var policy = JsonNode.Parse(resource.GetRawText())?.AsObject()
            ?? throw new CliException("The certificate policy could not be read back.");
        if (binding is not null)
        {
            policy["binding"] = binding;
            policy["sanMustMatchAccount"] = binding == "strict";
        }

        if (anchorsPem is not null)
        {
            policy["trustAnchorPem"] = anchorsPem.Length == 0 ? null : anchorsPem;
        }

        return await api.PutWithETagAsync(href, policy, etag, cancellationToken);
    }

    public static void Report(ILogger logger, JsonElement policy)
    {
        logger.LogInformation(
            "Binding: {Binding}. Issuer pattern: {Issuer}. Common-name pattern: {CommonName}. "
            + "RSA minimum: {RsaMinimum} bits. Curves: {Curves}. Required extended key usages: {Ekus}. Own trust anchors: {Anchor}. "
            + "Further rules: {Rules}.",
            Text(policy, "binding") ?? (Flag(policy, "sanMustMatchAccount") ? "strict" : "possession"),
            Text(policy, "issuerDnPattern") ?? "any",
            Text(policy, "subjectCnPattern") ?? "any",
            policy.TryGetProperty("minimumKeySizeBits", out var bits) && bits.ValueKind == JsonValueKind.Number
                ? bits.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{Number(policy, "defaultMinimumRsaKeySizeBits")} (default)",
            List(policy, "permittedCurves") is { Length: > 0 } curves ? curves : $"{List(policy, "defaultPermittedCurves")} (default)",
            List(policy, "requiredExtendedKeyUsages") is { Length: > 0 } ekus ? ekus : "none",
            Text(policy, "trustAnchorPem") is { } anchors
                ? $"{anchors.Split("-----BEGIN CERTIFICATE-----").Length - 1} certificate(s)"
                : "none",
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
