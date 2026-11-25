using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// Duplicate detection enforced at finalize (core #2476, owner's decision 2026-11-25): a caller that ASKS for the
// confirmation has the arriving bytes hashed against the archive and is answered 409 DUPLICATE_CONTENT with the
// candidates, unless it brought the user's explicit 'file anyway'. Absent the requirement the finalize stays lenient —
// every already-shipped client and every machine path files exactly as before.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DuplicateContentEnforcementTests
{
    private readonly E2EApiFactory _factory;

    public DuplicateContentEnforcementTests(E2EApiFactory factory) => _factory = factory;

    private static StringContent Content(string text) => new(text, Encoding.UTF8, "application/octet-stream");

    private static async Task<Guid> NewDocumentAsync(HttpClient client, Guid repo, string name, byte[] bytes, object? decision = null)
    {
        var doc = (await TestJson.Post(client, $"/api/documents/{repo}/children", new { name })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(client, $"/api/documents/{doc}/versions", new { fileExtension = ".txt" });
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;
        using var anon = new HttpClient();
        (await anon.PutAsync(uploadUrl, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        using var finalize = decision is null
            ? await client.PutAsJsonAsync($"/api/documents/{doc}/versions/{version.GetProperty("id").GetGuid()}", new { })
            : await client.PutAsJsonAsync($"/api/documents/{doc}/versions/{version.GetProperty("id").GetGuid()}", decision);
        if (finalize.StatusCode != HttpStatusCode.OK)
        {
            throw new ApiTestException(await finalize.Content.ReadAsStringAsync());
        }

        return doc;
    }

    private sealed class ApiTestException(string message) : Exception(message);

    [Fact]
    public async Task A_requested_confirmation_refuses_identical_content_with_the_candidates_then_allows_the_explicit_decision()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var client = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = (await TestJson.Post(client, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var bytes = Encoding.UTF8.GetBytes($"identical-{Guid.NewGuid():N}");

        var first = await NewDocumentAsync(client, repo, $"a{Guid.NewGuid():N}"[..9], bytes);

        // The second upload of the SAME bytes, asking for the confirmation: 409 with the candidate as data.
        var doc = (await TestJson.Post(client, $"/api/documents/{repo}/children", new { name = $"b{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(client, $"/api/documents/{doc}/versions", new { fileExtension = ".txt" });
        var uploadUrl = version.GetProperty("uploadUrl").GetString()!;
        using (var anon = new HttpClient())
        {
            (await anon.PutAsync(uploadUrl, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        using (var refused = await client.PutAsJsonAsync($"/api/documents/{doc}/versions/{version.GetProperty("id").GetGuid()}",
                   new { requireDuplicateConfirmation = true }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("DUPLICATE_CONTENT", problem.GetProperty("errorCode").GetString());
            Assert.Contains(problem.GetProperty("duplicates").EnumerateArray(), d => d.GetProperty("id").GetGuid() == first);
        }

        // The explicit "file anyway" finalizes the same pending version.
        using (var allowed = await client.PutAsJsonAsync($"/api/documents/{doc}/versions/{version.GetProperty("id").GetGuid()}",
                   new { requireDuplicateConfirmation = true, allowDuplicate = true }))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task Without_the_requirement_identical_content_files_as_before()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var client = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = (await TestJson.Post(client, "/api/repositories", new { name = $"r{Guid.NewGuid():N}"[..9] })).GetProperty("id").GetGuid();
        var bytes = Encoding.UTF8.GetBytes($"identical-{Guid.NewGuid():N}");

        await NewDocumentAsync(client, repo, $"a{Guid.NewGuid():N}"[..9], bytes);
        await NewDocumentAsync(client, repo, $"b{Guid.NewGuid():N}"[..9], bytes);   // legacy lenient: no decision sent
    }
}
