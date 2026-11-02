using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Has the certificate authority sign a token's CSR as a reader certificate (ADR 1037): the CSR, the holder's
/// address and the attestation go to the authority's admin API, the certificate comes back, and the enrolment door
/// then judges the attestation as for any certificate (ADR 0963). The authority is not the archive: a plain client
/// with the admin secret, which is typed by the administrator and never written anywhere.
/// </summary>
public static class CaSigning
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsCertificateRequest(string text) => text.Contains("BEGIN CERTIFICATE REQUEST", StringComparison.Ordinal);

    public static async Task<(string CertificatePem, string Serial)> SignAsync(
        HttpClient http, Uri authority, Guid tenantId, string csrPem, string holderEmail, AttestationFiles? attestation, CancellationToken cancellationToken)
    {
        // The authority's path, composed from its parts: the "tenants" word alone is not an object-key prefix.
        var path = string.Join('/', "tenants", tenantId.ToString("D"), "sign");
        using var answer = await http.PostAsJsonAsync(new Uri(authority, path),
            new { csrPem, holderEmail, attestation }, Json, cancellationToken);
        if (!answer.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                code = (await answer.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)).GetProperty("errorCode").GetString();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
            {
                // Not a problem document: the status alone names it.
            }

            throw new CliException($"The certificate authority refused to sign: {(int)answer.StatusCode}{(code is null ? string.Empty : $" {code}")}.");
        }

        var issued = await answer.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
        return (issued.GetProperty("certificatePem").GetString()!, issued.GetProperty("serial").GetString() ?? string.Empty);
    }

    public static HttpClient Client(string secret)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return http;
    }
}
