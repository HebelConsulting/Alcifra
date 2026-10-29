using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

// Drop the link, keep the resource (#1843, ADR 0953). Without an encryption service, an object encrypted at rest
// cannot be presigned for reading. A resource that only EMBEDS such a link must still answer, with the link absent
// (as the strict tier's null makes it absent), so the document's details stay reachable; FOLLOWING a link to it
// answers 503 ENCRYPTION_SERVICE_NOT_CONFIGURED.
//
// The host here has a service configured, so the no-service guard is rehearsed for the keys each test names
// (E2EApiFactory.PresignGuardRehearsal) — the real guard, probing the real store's metadata.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class RefusedPresignDropsTheLinkTests
{
    private readonly E2EApiFactory _factory;

    public RefusedPresignDropsTheLinkTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_version_whose_object_is_refused_answers_without_its_download_and_preview_links()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refused-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Refused Owner", canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Refused {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"letterhead-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("this stands in for ciphertext")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });
        var versionUrl = $"/api/documents/{docId}/versions/{versionId}";

        // Anti-vacuous: before the object is marked, the resource DOES carry both links.
        Assert.Contains("download", Rels(await TestJson.Get(owner, versionUrl)));
        Assert.Contains("preview", Rels(await TestJson.Get(owner, versionUrl)));

        var objectKey = await VersionObjectKeyAsync(versionId);
        await MarkEncryptedAsync(objectKey);
        try
        {
            var response = await owner.GetAsync(versionUrl);

            // THE ASSERTION: the resource answers, and only the two refused links are gone.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rels = Rels(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
            Assert.DoesNotContain("download", rels);
            Assert.DoesNotContain("preview", rels);
            Assert.Contains("self", rels);

            // And the document's own details stay reachable.
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{docId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{docId}/versions")).StatusCode);
        }
        finally
        {
            _factory.PresignGuardedKeys.TryRemove(objectKey, out _);
        }
    }

    [Fact]
    public async Task An_intray_item_lists_without_its_download_link_and_following_its_preview_answers_503()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refused-intray-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "u-1234", "Refused Intray");
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var itemName = $"invoice-template-{Guid.NewGuid():N}.txt";
        var upload = await TestJson.Post(owner, "/api/intray", new { fileName = itemName });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("this stands in for ciphertext")))).EnsureSuccessStatusCode();
        }

        var objectKey = ObjectKeyPrefixes.UserInbox(tenantId, userId) + itemName;
        await MarkEncryptedAsync(objectKey);
        try
        {
            // Embedding: the listing answers, the item is there, its download link is not.
            var listing = await owner.GetAsync("/api/intray");
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            var item = JsonDocument.Parse(await listing.Content.ReadAsStringAsync()).RootElement
                .GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == itemName);
            Assert.DoesNotContain("download", Rels(item));
            Assert.Contains("preview", Rels(item)); // a route of ours, not a presign: following it is what refuses

            // Following: the explicit preview presign answers the reads' own 503.
            var preview = await owner.GetAsync($"/api/intray/{itemName}/preview");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, preview.StatusCode);
            Assert.Contains("ENCRYPTION_SERVICE_NOT_CONFIGURED", await preview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            _factory.PresignGuardedKeys.TryRemove(objectKey, out _);
        }
    }

    /// <summary>Gives the stored object the at-rest marker and puts its key under the rehearsed guard.</summary>
    private async Task MarkEncryptedAsync(string objectKey)
    {
        await _factory.Services.GetRequiredService<S3ObjectStorageClient>().SetObjectMetadataAsync(
            objectKey,
            new Dictionary<string, string>
            {
                [AtRestMetadata.WrappedDekKey] = "not-a-real-wrapped-key",
                [AtRestMetadata.KekGenerationKey] = "kek-v1",
            });
        _factory.PresignGuardedKeys[objectKey] = true;
    }

    private async Task<string> VersionObjectKeyAsync(Guid versionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.Id == versionId).Select(v => v.ObjectKey).SingleAsync();
    }

    private static List<string> Rels(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!).ToList();
}
