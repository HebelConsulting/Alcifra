using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// A corrected issuing date says so ON THE VERSION it corrected (#2472, ADR 1110) — and it says so BESIDE the
// uploader's comment, never instead of it. The note is a closed-list token stored in DocumentVersion.SystemComment
// (its own column), which every surface resolves into the reader's language; the person's "why" and the core's
// "what else happened" are two facts about one version, so neither may overwrite the other.
//
// The two halves that could silently rot: writing the token on a PUT that changed NOTHING (a client echoing the
// form back would label every version "corrected"), and losing the uploader's comment when the note lands.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class DocumentDateCorrectionTests
{
    private readonly E2EApiFactory _factory;

    public DocumentDateCorrectionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Correcting_a_date_leaves_the_token_beside_the_uploaders_comment()
    {
        using var owner = await NewOwnerAsync("correct");
        var (docId, versionId) = await FileVersionAsync(owner, comment: "Invoice total corrected");

        var filed = await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}");
        Assert.Null(TextField(filed, "systemComment")); // nothing was corrected yet

        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date",
            new { documentDate = "2020-01-02" })).EnsureSuccessStatusCode();

        var corrected = await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}");
        Assert.Equal("{documentversion.date-corrected}", TextField(corrected, "systemComment"));
        Assert.Equal("Invoice total corrected", TextField(corrected, "comment")); // never overwritten

        // The LIST carries both too — the versions dialog reads the list, not one version at a time.
        var list = await TestJson.Get(owner, $"/api/documents/{docId}/versions");
        var row = list.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == versionId);
        Assert.Equal("{documentversion.date-corrected}", TextField(row, "systemComment"));
        Assert.Equal("Invoice total corrected", TextField(row, "comment"));
    }

    [Fact]
    public async Task Setting_the_same_pair_again_is_not_a_correction()
    {
        using var owner = await NewOwnerAsync("echo");
        var (docId, versionId) = await FileVersionAsync(owner, comment: null);

        // Round-trip the form the way a client does: write back what was read, changing nothing.
        var filed = await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}");
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date",
            new { documentDate = TextField(filed, "documentDate") })).EnsureSuccessStatusCode();

        var after = await TestJson.Get(owner, $"/api/documents/{docId}/versions/{versionId}");
        Assert.Null(TextField(after, "systemComment"));
    }

    private async Task<HttpClient> NewOwnerAsync(string tag)
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"corr-{tag}-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Correction Owner", canManageRepositories: true);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));
    }

    // Repository → document → one confirmed version carrying an optional comment, the ordinary upload walk.
    private static async Task<(Guid DocumentId, Guid VersionId)> FileVersionAsync(HttpClient owner, string? comment)
    {
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Corr {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"invoice-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("Rechnung 2026-001")))).EnsureSuccessStatusCode();
        }

        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { comment })).EnsureSuccessStatusCode();
        return (docId, versionId);
    }

    // Absent, null and a real string are three different things on the wire — a resource that omits the member
    // must read as "no note", not fail the test.
    private static string? TextField(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
