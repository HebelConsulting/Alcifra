using System.Text.Json;
using Alcifra.SealedArchive;

namespace Alcifra.ApiClient.Inspection;

/// <summary>
/// A sealed file's inspection as a client shows it (#2291): from the server for a document (its <c>seal-inspection</c> rel),
/// or made here from a local file, so both look the same.
/// </summary>
public sealed record SealReport(
    SealedKind Kind,
    string Format,
    bool SmimeWrapped,
    long Size,
    string? ContentType,
    string? ContentEncryption,
    IReadOnlyList<SealReportRecipient> Recipients,
    IReadOnlyList<InspectedSigner> Signers,
    IReadOnlyList<string> Certificates,
    SealReportAudience? Audience,
    IReadOnlyList<Asn1Node> Asn1)
{
    /// <summary>A local file's inspection, its recipients matched to the installation's certificates the caller may see.</summary>
    public static SealReport Of(SealedInspection inspection, IReadOnlyList<KnownCertificate> known) => new(
        inspection.Kind, inspection.Format, inspection.SmimeWrapped, inspection.Size, inspection.ContentType, inspection.ContentEncryption,
        [.. inspection.Recipients.Select(r => known.FirstOrDefault(k => k.Is(r.Reference)) is { } k
            ? new SealReportRecipient(k.HolderName, k.HolderEmail, k.Label, k.RevokedAt, k.Attestation, null, r.Reference, r.Method, r.KeyEncryptionAlgorithm, r.OriginatorKeyAlgorithm, null)
            : new SealReportRecipient(null, null, null, null, null, null, r.Reference, r.Method, r.KeyEncryptionAlgorithm, r.OriginatorKeyAlgorithm, null))],
        inspection.Signers, inspection.Certificates, null,
        inspection.Cms is { } cms ? Asn1Tree.Of(cms) : []);

    /// <summary>The server's answer, as the <c>seal-inspection</c> rel returns it.</summary>
    public static SealReport From(JsonElement json) => new(
        Enum.TryParse<SealedKind>(Text(json, "kind"), out var kind) ? kind : SealedKind.NotSealed,
        Text(json, "format") ?? string.Empty,
        json.TryGetProperty("smimeWrapped", out var smime) && smime.GetBoolean(),
        json.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
        Text(json, "contentType"),
        Text(json, "contentEncryption"),
        [.. Array(json, "recipients").Select(r => new SealReportRecipient(
            Text(r, "holderName"), Text(r, "holderEmail"), Text(r, "label"), Moment(r, "revokedAt"), Text(r, "attestation"), Moment(r, "notAfter"),
            new CmsRecipient(Text(r, "issuer"), Text(r, "serial"), Text(r, "subjectKeyIdentifier")),
            Text(r, "method") ?? string.Empty, Text(r, "keyEncryptionAlgorithm") ?? string.Empty, Text(r, "originatorKeyAlgorithm"),
            r.TryGetProperty("inAudience", out var inAudience) && inAudience.ValueKind is JsonValueKind.True or JsonValueKind.False ? inAudience.GetBoolean() : null))],
        [.. Array(json, "signers").Select(s => new InspectedSigner(
            Text(s, "subject"), Text(s, "issuer"), Text(s, "serial"), Text(s, "digestAlgorithm") ?? string.Empty, Moment(s, "signingTime"),
            s.TryGetProperty("signatureValid", out var valid) && valid.ValueKind is JsonValueKind.True or JsonValueKind.False ? valid.GetBoolean() : null))],
        [.. Array(json, "certificates").Select(c => c.GetString() ?? string.Empty)],
        json.TryGetProperty("audience", out var audience) && audience.ValueKind == JsonValueKind.Object
            ? new SealReportAudience(
                audience.GetProperty("matches").GetBoolean(),
                [.. Array(audience, "missing").Select(m => new SealReportMissing(Text(m, "person") ?? string.Empty, Text(m, "thumbprint") ?? string.Empty))])
            : null,
        [.. Array(json, "asn1").Select(n => new Asn1Node(
            n.GetProperty("offset").GetInt32(), n.GetProperty("depth").GetInt32(), n.GetProperty("headerLength").GetInt32(),
            n.GetProperty("length").GetInt32(), n.GetProperty("constructed").GetBoolean(), Text(n, "tag") ?? string.Empty, Text(n, "value")))]);

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Moment(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : null;

    private static IEnumerable<JsonElement> Array(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

/// <summary>One recipient: who the installation says it is (null fields: an unknown recipient), and how the envelope names it.</summary>
/// <param name="InAudience">False: no longer in the declared audience, yet able to open this version. Null: no audience to compare.</param>
public sealed record SealReportRecipient(
    string? HolderName, string? HolderEmail, string? Label, DateTimeOffset? RevokedAt, string? Attestation, DateTimeOffset? NotAfter,
    CmsRecipient Reference, string Method, string KeyEncryptionAlgorithm, string? OriginatorKeyAlgorithm, bool? InAudience)
{
    /// <summary>The installation knows the certificate this recipient names.</summary>
    public bool IsKnown => Label is not null;
}

/// <summary>Whether the envelope matches the declared audience, and who is in the audience but not in it.</summary>
public sealed record SealReportAudience(bool Matches, IReadOnlyList<SealReportMissing> Missing);

public sealed record SealReportMissing(string Person, string Thumbprint);

/// <summary>Reads a document's seal inspection from its <c>seal-inspection</c> rel (#2291).</summary>
public sealed class SealInspectionClient(ApiCore core)
{
    public async Task<SealReport> GetAsync(string inspectionHref, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.GetAsync(inspectionHref, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The seal could not be inspected", cancellationToken);
        return SealReport.From(await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<JsonElement>(response.Content, cancellationToken));
    }
}
