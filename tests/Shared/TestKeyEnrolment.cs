using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alcifra.TestSupport;

/// <summary>
/// Enrols a throwaway key for the signed-in caller of a running installation (#2278), for the suites that show and revoke
/// keys. Switching enrolment on is a tenant setting, so the returned scope puts the settings back as they were.
/// </summary>
/// <remarks>A wart, said so: it changes the shared demo tenant for the length of a test (ADR 0884).</remarks>
public static class TestKeyEnrolment
{
    /// <summary>
    /// Enrols a self-signed key for <paramref name="email"/> under <paramref name="label"/> through the caller's rels, with the
    /// Reader certificates folder under the demo seed's "Demo Repository".
    /// </summary>
    public static async Task<IAsyncDisposable> EnrolAsync(HttpClient http, string email, string label) =>
        (await EnrolKeyAsync(http, email, label)).Restore;

    /// <summary>The same, with the Reader certificates folder under <paramref name="parentFolderId"/>.</summary>
    public static async Task<IAsyncDisposable> EnrolAsync(HttpClient http, string email, string label, Guid parentFolderId) =>
        (await EnrolKeyAsync(http, email, label, parentFolderId)).Restore;

    /// <summary>
    /// The same, handing back the enrolled certificate WITH its private key, for a test that opens what is sealed to it
    /// (a CI/CD secret, ADR 1084), under the demo seed's "Demo Repository".
    /// </summary>
    public static async Task<(IAsyncDisposable Restore, X509Certificate2 Key)> EnrolKeyAsync(HttpClient http, string email, string label)
    {
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var demo = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        return await EnrolKeyAsync(http, email, label, demo);
    }

    private static async Task<(IAsyncDisposable Restore, X509Certificate2 Key)> EnrolKeyAsync(HttpClient http, string email, string label, Guid parentFolderId)
    {
        var root = Links(await http.GetFromJsonAsync<JsonElement>("/api"));
        var settings = root["certificateSettings"];
        var original = await ReadAsync(http, settings);
        var restore = new Restore(http, settings, original.Body);

        var on = original.Body.DeepClone().AsObject();
        on["selfServiceEnabled"] = true;
        on["parentFolderId"] = parentFolderId;
        on["unattestedHardwareAllowed"] = true;
        await PutAsync(http, settings, on, original.ETag);

        var key = SelfSigned(email);
        using var enrolled = await http.PostAsJsonAsync(root["certificates"], new { certificatePem = key.ExportCertificatePem(), label });
        if (!enrolled.IsSuccessStatusCode)
        {
            await restore.DisposeAsync();
            throw new InvalidOperationException($"Enrolling the test key failed: {await enrolled.Content.ReadAsStringAsync()}");
        }

        return (restore, key);
    }

    private sealed class Restore(HttpClient http, string href, JsonNode body) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await PutAsync(http, href, body, (await ReadAsync(http, href)).ETag);
    }

    private static async Task<(JsonNode Body, string ETag)> ReadAsync(HttpClient http, string href)
    {
        using var response = await http.GetAsync(href);
        response.EnsureSuccessStatusCode();
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())!, response.Headers.ETag!.Tag);
    }

    private static async Task PutAsync(HttpClient http, string href, JsonNode body, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // The certificate with its private key: only its public half is ever sent.
    private static X509Certificate2 SelfSigned(string email)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test key", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static Dictionary<string, string> Links(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray()
            .GroupBy(l => l.GetProperty("rel").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("href").GetString()!);
}
