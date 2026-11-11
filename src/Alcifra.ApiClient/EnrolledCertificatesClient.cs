using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>
/// The caller's enrolled reader certificates and what each one's attestation is worth (ADR 0963), followed from the root's
/// <c>certificates</c> rel: present while a module licensing enrolment is active, absent otherwise (an empty list here).
/// </summary>
public sealed class EnrolledCertificatesClient(ApiCore core)
{
    /// <summary>A key's attestation as read from the token: the request's <c>attestation</c> object.</summary>
    public sealed record KeyAttestation(string Format, string AttestationCertificatePem, string IntermediateCertificatePem, string? Slot);

    /// <summary>One enrolled certificate as the listing shows it.</summary>
    /// <param name="Attestation">"attested", "rootNotTrusted" or "notAttested".</param>
    /// <param name="Usable">Whether it addresses its holder now.</param>
    public sealed record Enrolled(string Label, string Serial, DateTimeOffset? NotAfter, bool Usable, string Attestation, string? AttestationRoot, string? DeviceSerial)
    {
        /// <summary>The certificate's issuer, which with the serial names it as an envelope recipient does (ADR 0969);
        /// null from an installation older than that.</summary>
        public string? Issuer { get; init; }

        /// <summary>When it was revoked; null while it is not (#2278).</summary>
        public DateTimeOffset? RevokedAt { get; init; }

        /// <summary>Why: a reason's code (<c>CertificateRevocationReasons</c>) or the words given for "Other".</summary>
        public string? RevocationReason { get; init; }

        /// <summary>The key's <c>revoke</c> rel: present where the caller may revoke it (its holder, a user administrator).</summary>
        public string? RevokeHref { get; init; }
    }

    /// <summary>The caller's own enrolled certificates; empty where the installation offers no enrolment.</summary>
    public async Task<IReadOnlyList<Enrolled>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).TryGetValue("certificates", out var href))
        {
            return [];
        }

        var listing = await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        return listing.ValueKind == JsonValueKind.Array ? [.. listing.EnumerateArray().Select(Parse)] : [];
    }

    /// <summary>
    /// The installation's certificates as the caller may see them (an administrator every holder, a user their own), in the
    /// shape a sealed file's recipients are matched against (#2007, #2291); empty where the installation offers no enrolment.
    /// </summary>
    public async Task<IReadOnlyList<Inspection.KnownCertificate>> GetKnownAsync(CancellationToken cancellationToken = default) =>
        (await core.GetRootLinksAsync(cancellationToken)).TryGetValue("certificates", out var href)
            ? Known(await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken))
            : [];

    /// <summary>A certificate listing's rows as recipients are matched against them: one parse for the CLI and the desktop.</summary>
    public static IReadOnlyList<Inspection.KnownCertificate> Known(JsonElement listing)
    {
        var items = listing.ValueKind == JsonValueKind.Array ? listing : listing.TryGetProperty("items", out var i) ? i : default;
        return items.ValueKind != JsonValueKind.Array ? [] : [.. items.EnumerateArray().Select(c =>
        {
            string? Text(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Inspection.KnownCertificate(
                Text("issuer"), Text("serial"), Text("subjectKeyIdentifier"), Text("label") ?? string.Empty,
                Text("holderName"), Text("holderEmail"),
                c.TryGetProperty("revokedAt", out var r) && r.ValueKind == JsonValueKind.String ? r.GetDateTimeOffset() : null,
                Text("attestation") ?? "notAttested");
        }).OrderBy(k => k.RevokedAt is not null)];   // an unrevoked enrolment names a recipient before a revoked one (#2317)
    }

    /// <summary>The keys at a listing a rel named: a user's <c>certificates</c> in the Users tab (#2278).</summary>
    public async Task<IReadOnlyList<Enrolled>> GetAtAsync(string href, CancellationToken cancellationToken = default)
    {
        var listing = await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        return listing.ValueKind == JsonValueKind.Array ? [.. listing.EnumerateArray().Select(Parse)] : [];
    }

    /// <summary>
    /// Revokes a key through its <c>revoke</c> rel with a reason (#2278, ADR 1083). Null when it is revoked, else the
    /// server's error code (empty when the refusal carried none), for <c>ApiErrorText</c>.
    /// </summary>
    public async Task<string?> RevokeAsync(string revokeHref, string reason, string? text, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(revokeHref, new { reason, text }, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return problem.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The caller's enrolled certificates as keys an envelope's recipients can be matched against (ADR 0969), or null
    /// where that cannot be said: no enrolment offered here, or an installation whose listing names no issuer.
    /// </summary>
    public async Task<IReadOnlyList<EnrolledKey>?> GetMineForMatchingAsync(CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).ContainsKey("certificates"))
        {
            return null;
        }

        var mine = await GetMineAsync(cancellationToken);
        return mine.All(e => e.Issuer is { Length: > 0 })
            ? [.. mine.Select(e => new EnrolledKey(e.Issuer!, e.Serial, e.Label))]
            : null;
    }

    internal static Enrolled Parse(JsonElement row)
    {
        string? Text(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Enrolled(
            Text("label") ?? string.Empty,
            Text("serial") ?? string.Empty,
            DateTimeOffset.TryParse(Text("notAfter"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var until) ? until : null,
            row.TryGetProperty("usable", out var usable) && usable.ValueKind == JsonValueKind.True,
            Text("attestation") ?? "notAttested",
            Text("attestationRoot"),
            Text("deviceSerial"))
        {
            Issuer = Text("issuer"),
            RevokedAt = DateTimeOffset.TryParse(Text("revokedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var revoked) ? revoked : null,
            RevocationReason = Text("revocationReason"),
            RevokeHref = ApiCore.ParseLinks(row)?.Href("revoke"),
        };
    }
}
