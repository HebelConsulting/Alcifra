using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateListSettings : TenantSessionSettings
{
    [CommandOption("--holder")]
    [Description("Only this holder's, by e-mail (an administrator's question). Omitted lists your own, or the whole tenant's when you administer it.")]
    public string? Holder { get; init; }
}

/// <summary>
/// Reports what is enrolled (#1504) — including the rows that are NOT usable and why.
/// </summary>
/// <remarks>
/// <para>
/// Expired and revoked rows are listed on purpose: seeing the revoked one is how a holder confirms a
/// revocation took, and the module's listing deliberately does not filter by usability for that reason
/// (its ADR 0005). A listing that showed only what works would be unable to answer the question people
/// actually ask, which is "why can this person not open anything?".
/// </para>
/// <para>
/// So the columns carry the ANSWERS, not just the facts: <c>usable</c> is the module's own verdict, and
/// <c>fails policy</c> is the tenant's rule this certificate would no longer satisfy (ADR 0006) — which is
/// flagged rather than enforced backwards, so a row can be enrolled and failing at the same time.
/// </para>
/// </remarks>
public sealed class CertificateListCommand(ILogger<CertificateListCommand> logger) : AsyncCommand<CertificateListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateListSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var address = await CertificateSurface.AddressAsync(new Hypermedia(api), cancellationToken);

        // The holder rides as a QUERY on the advertised href, which is following rather than composing: the
        // server owns the path, the client owns the filter (ADR 0557's boundary).
        if (settings.Holder is { Length: > 0 } holder)
        {
            address += $"?holder={Uri.EscapeDataString(holder)}";
        }

        var rows = Rows(await api.GetAsync(address, cancellationToken)).ToList();
        if (rows.Count == 0)
        {
            if (settings.Holder is { Length: > 0 })
            {
                logger.LogInformation("Nothing is enrolled for {Holder}.", settings.Holder);
            }
            else
            {
                logger.LogInformation("Nothing is enrolled in this tenant.");
            }

            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("label");
        table.AddColumn("serial");
        table.AddColumn("valid until");
        table.AddColumn("state");
        table.AddColumn("attestation");

        foreach (var row in rows)
        {
            table.AddRow(
                Markup.Escape(row.Label),
                Markup.Escape(row.Serial.Length > 0 ? row.Serial : "—"),
                row.NotAfter?.ToString("yyyy-MM-dd") ?? "—",
                State(row),
                Attestation(row));
        }

        logger.LogInformation("{Table}", CliLogging.Render(table));
        return 0;
    }

    /// <summary>The one column a reader acts on: what is wrong, in the order it matters.</summary>
    /// <remarks>
    /// Revoked first, because it is a decision somebody made and outranks everything else; then expiry, a
    /// fact about the certificate; then the tenant's policy, which is a rule that changed under it. Reporting
    /// them in any other order would tell a reader to fix the least important thing.
    /// </remarks>
    private static string State(Enrolment row) => row switch
    {
        { RevokedAt: { } when } => $"[red]revoked[/] {when.ToUniversalTime():yyyy-MM-dd HH:mm} UTC",
        { NotAfter: { } until } when until < DateTimeOffset.UtcNow => "[red]expired[/]",
        { FailsPolicy: { Length: > 0 } why } => $"[yellow]fails policy[/]: {Markup.Escape(why)}",
        { Usable: false } => "[red]not usable[/]",
        _ => "[green]usable[/]",
    };

    /// <summary>What the key's attestation is worth here (ADR 0963): by which root and token, or why not.</summary>
    internal static string Attestation(Enrolment row) => row.Attestation switch
    {
        "attested" => $"[green]attested[/] by {Markup.Escape(row.AttestationRoot)}{(row.DeviceSerial.Length > 0 ? $", token {Markup.Escape(row.DeviceSerial)}" : string.Empty)}",
        "rootNotTrusted" => $"[yellow]root switched off[/]: {Markup.Escape(row.AttestationRoot)}",
        _ => "[yellow]not attested[/] — enrol again from the token",
    };

    internal static IEnumerable<Enrolment> Rows(JsonElement listing)
    {
        // The core answers a bare array (ADR 0936). The wrapped shapes are what the Encryption Module answered before
        // enrolment moved into the core; read either, so an older installation still lists.
        JsonElement rows;
        if (listing.ValueKind == JsonValueKind.Array)
        {
            rows = listing;
        }
        else if (!listing.TryGetProperty("certificates", out rows) && !listing.TryGetProperty("enrolled", out rows))
        {
            yield break;
        }

        foreach (var row in rows.EnumerateArray())
        {
            yield return new Enrolment(
                Text(row, "label"),
                Text(row, "serial"),
                Moment(row, "notAfter"),
                Moment(row, "revokedAt"),
                Text(row, "failsPolicy"),
                !row.TryGetProperty("usable", out var usable) || usable.GetBoolean(),
                Text(row, "attestation"),
                Text(row, "attestationRoot"),
                Text(row, "deviceSerial"));
        }
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? Moment(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;

    internal sealed record Enrolment(
        string Label,
        string Serial,
        DateTimeOffset? NotAfter,
        DateTimeOffset? RevokedAt,
        string FailsPolicy,
        bool Usable,
        string Attestation,
        string AttestationRoot,
        string DeviceSerial);
}
