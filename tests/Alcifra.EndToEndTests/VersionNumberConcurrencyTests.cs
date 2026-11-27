using System.Net;
using System.Text;

namespace Alcifra.EndToEndTests;

// ADR 1113 over the REAL API and Postgres: finalizes of one document are serialized by the per-document advisory
// lock, so concurrent uploads all succeed and the numbers are exactly 1..N — no unique-violation 500 and no shared
// number. The exhaustive case (a duplicate refused by the index) is DocumentVersionNumberUniquenessTests; the
// deterministic lock proof is DocumentVersionNumberLockTests.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class VersionNumberConcurrencyTests
{
    private readonly E2EApiFactory _factory;

    public VersionNumberConcurrencyTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Concurrent_uploads_of_one_document_get_distinct_numbers()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Conc {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "concurrent-doc" })).GetProperty("id").GetGuid();

        const int uploads = 4;
        var versionIds = await Task.WhenAll(Enumerable.Range(0, uploads)
            .Select(index => UploadAsync(api, docId, $"payload {index} {Guid.NewGuid():N}\n")));

        var list = await TestJson.Get(api, $"/api/documents/{docId}/versions");
        var numbers = list.GetProperty("versions").EnumerateArray()
            .Where(version => version.GetProperty("status").GetString() == "Confirmed")
            .Select(version => version.GetProperty("versionNumber").GetInt32())
            .Order()
            .ToArray();

        Assert.Equal(Enumerable.Range(1, uploads), numbers);
        Assert.Equal(uploads, versionIds.Distinct().Count());
    }

    private static async Task<Guid> UploadAsync(HttpClient api, Guid docId, string content)
    {
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content))))
                .EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return versionId;
    }
}
