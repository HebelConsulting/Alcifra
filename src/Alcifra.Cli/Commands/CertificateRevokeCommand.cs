using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class CertificateRevokeSettings : TenantSessionSettings
{
    [CommandOption("--serial")]
    [Description("The certificate's serial — what a certification authority names it by.")]
    public string? Serial { get; init; }

    [CommandOption("--thumbprint")]
    [Description("The certificate's SHA-256 thumbprint, as the listing reports it.")]
    public string? Thumbprint { get; init; }

    [CommandOption("--on")]
    [Description("When it was revoked: an instant (2026-10-05T14:30:00+02:00) or a day (yyyy-MM-dd, meaning its start in UTC). Defaults to now.")]
    public string? On { get; init; }

    [CommandOption("--reason")]
    [Description("Why it is revoked: lost, compromised, replaced, left, or other (with --text). Required unless --on records a revocation made elsewhere.")]
    public string? Reason { get; init; }

    [CommandOption("--text")]
    [Description("With --reason other: the reason in your own words, at most 200 characters.")]
    public string? Text { get; init; }

    /// <summary>The reasons the server's revoke transition accepts (#2278, ADR 1083).</summary>
    internal static readonly IReadOnlyList<string> Reasons = ["lost", "compromised", "replaced", "left", "other"];

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,

        // EXACTLY ONE, because the two identify the certificate differently and accepting both would leave
        // the question of what to do when they disagree — which is a question with no good answer.
        _ when string.IsNullOrWhiteSpace(Serial) == string.IsNullOrWhiteSpace(Thumbprint) =>
            ValidationResult.Error("Give exactly one of --serial or --thumbprint."),

        _ when On is { Length: > 0 } && RevokedAt(On) is null =>
            ValidationResult.Error($"'{On}' is neither an instant nor a date. Use 2026-10-05T14:30:00+02:00 or yyyy-MM-dd."),

        _ when ReasonProblem(On, Reason, Text) is { } problem => ValidationResult.Error(problem),

        _ => ValidationResult.Success(),
    };

    /// <summary>What is wrong with the reason given, or null when nothing is.</summary>
    /// <remarks>
    /// --on records what a certification authority revoked at its own instant, as the bulk import does; a revocation made
    /// NOW goes through the key's revoke transition, which asks why (#2278, ADR 1083).
    /// </remarks>
    internal static string? ReasonProblem(string? on, string? reason, string? text) => (on, reason) switch
    {
        ({ Length: > 0 }, _) when reason is { Length: > 0 } || text is { Length: > 0 } =>
            "--on records a revocation made elsewhere and takes no --reason. Leave out --on to revoke now.",
        ({ Length: > 0 }, _) => null,
        (_, { Length: > 0 }) when !Reasons.Contains(reason) => $"'{reason}' is no reason. Give --reason: {string.Join(", ", Reasons)}.",
        (_, null or "") => $"Give --reason: {string.Join(", ", Reasons)}.",
        (_, "other") when text?.Trim() is not { Length: > 0 and <= 200 } => "--reason other needs --text, at most 200 characters.",
        _ => null,
    };

    /// <summary>
    /// The moment <paramref name="on"/> names: an instant as written, or the start of a bare day in UTC.
    /// </summary>
    /// <remarks>
    /// An instant WITHOUT an offset is refused rather than read in this machine's zone: the field stores a
    /// point in time, and an operator's local clock is not something the archive can see or record.
    /// </remarks>
    internal static DateTimeOffset? RevokedAt(string on) => on switch
    {
        _ when DateOnly.TryParseExact(on, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) =>
            new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ when on.Contains('T') && (on.EndsWith('Z') || on.LastIndexOfAny(['+', '-']) > on.IndexOf('T'))
            && DateTimeOffset.TryParse(on, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) => instant,
        _ => null,
    };
}

/// <summary>
/// Revokes one enrolled certificate (#1504), by the identifier the caller has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Revoking now is the key's own transition</b> (#2278, ADR 1083): its <c>revoke</c> rel, with a reason, the same one
/// the clients' dialogs use, open to the key's holder and to a user administrator. <c>--on</c> instead RECORDS what a
/// certification authority revoked at its own instant by writing <c>Revoked on</c>, as the bulk import does.
/// </para>
/// <para>
/// <b>Two identifiers because two callers have different ones.</b> A certification authority names what it
/// revoked by SERIAL and mentions a thumbprint nowhere; a person reading the listing has the thumbprint in
/// front of them. Both resolve through the same listing, and the walk that writes the field is shared with
/// the bulk import so the two cannot write it differently.
/// </para>
/// </remarks>
public sealed class CertificateRevokeCommand(ILogger<CertificateRevokeCommand> logger) : AsyncCommand<CertificateRevokeSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateRevokeSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var api = new AlcifraApi(http);
        var hypermedia = new Hypermedia(api);
        var address = await CertificateSurface.AddressAsync(hypermedia, cancellationToken);

        var listing = await api.GetAsync(address, cancellationToken);
        if (Find(listing, settings) is not { } found)
        {
            throw new CliException(settings.Serial is { Length: > 0 }
                ? $"Nothing enrolled in this tenant carries serial {settings.Serial}. "
                    + "Check it with: alcifra certificates list"
                : $"Nothing enrolled in this tenant carries thumbprint {settings.Thumbprint}.");
        }

        if (found.AlreadyRevoked)
        {
            // The state asked for already obtains. Not an error, and not silently "done" either — an operator
            // running this deliberately is entitled to know they changed nothing.
            logger.LogInformation("Unchanged: {Certificate} was already revoked.", found.Label);
            return 0;
        }

        if (settings.On is { Length: > 0 } given)
        {
            var on = CertificateRevokeSettings.RevokedAt(given)!.Value;
            await CertificateRevocation.SetRevokedOnAsync(api, hypermedia, found.DocumentId, on, cancellationToken);
            logger.LogInformation(
                "Recorded {Certificate} as revoked on {RevokedAt:yyyy-MM-dd HH:mm:ss} UTC. Content is no longer enveloped to it.",
                found.Label, on.ToUniversalTime());
            return 0;
        }

        // The key's own transition (#2278): its holder or a user administrator, with a reason. No rel, not yours to revoke.
        var revoke = found.RevokeHref ?? throw new CliException(
            $"You may not revoke {found.Label}: only its holder or an administrator who manages users may.");
        await api.PostAsync(revoke, new { reason = settings.Reason, text = settings.Text?.Trim() }, cancellationToken);

        logger.LogInformation(
            "Revoked {Certificate} ({Reason}). Content is no longer enveloped to it. A reader whose only certificate this was "
            + "can no longer open sealed documents until another is enrolled.", found.Label, settings.Reason);

        return 0;
    }

    private static Enrolment? Find(JsonElement listing, CertificateRevokeSettings settings)
    {
        if (!listing.TryGetProperty("certificates", out var rows) && !listing.TryGetProperty("enrolled", out rows))
        {
            return null;
        }

        var serial = settings.Serial is { Length: > 0 } s ? CertificateManifest.NormaliseSerial(s) : null;
        var thumbprint = settings.Thumbprint?.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("documentId", out var id))
            {
                continue;
            }

            var rowSerial = row.TryGetProperty("serial", out var rs) ? CertificateManifest.NormaliseSerial(rs.GetString() ?? string.Empty) : string.Empty;
            var rowThumb = row.TryGetProperty("thumbprint", out var rt) ? (rt.GetString() ?? string.Empty).ToUpperInvariant() : string.Empty;

            var matches = serial is { Length: > 0 }
                ? string.Equals(rowSerial, serial, StringComparison.Ordinal)
                : string.Equals(rowThumb, thumbprint, StringComparison.Ordinal);

            if (matches)
            {
                return new Enrolment(
                    id.GetGuid(),
                    row.TryGetProperty("label", out var l) ? l.GetString() ?? "the certificate" : "the certificate",
                    row.TryGetProperty("revokedAt", out var at) && at.ValueKind is not JsonValueKind.Null,
                    Hypermedia.LinksOf(row).TryGetValue("revoke", out var revoke) ? revoke : null);
            }
        }

        return null;
    }

    private sealed record Enrolment(Guid DocumentId, string Label, bool AlreadyRevoked, string? RevokeHref);
}
