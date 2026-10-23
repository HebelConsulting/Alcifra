using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.ApiClient;

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
    public sealed record Enrolled(string Label, string Serial, DateTimeOffset? NotAfter, bool Usable, string Attestation, string? AttestationRoot, string? DeviceSerial);

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
            Text("deviceSerial"));
    }
}
