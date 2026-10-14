using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// The offline export (ADR 0927; #1759): what the caller may read, as the regular exporter writes it, for the phone's
// offline copy. The owner's "ultimate test" starts here: for a caller who may read everything it IS the regular
// export, plus the folder chain above its anchor; for anyone else it is that export cut to what they may read.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class OfflineExportTests
{
    private readonly E2EApiFactory _factory;

    public OfflineExportTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Setup(Guid TenantId, HttpClient Owner, HttpClient Admin, Guid RepoId, Guid FolderId, Guid ReportId, Guid NotesId);

    private async Task<Setup> SetUpAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "export-1234", "Admin");
        await _factory.GrantTenantAdminAsync(email);
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "export-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Offline {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Contracts" })).GetProperty("id").GetGuid();
        var reportId = await DocumentWithContentAsync(owner, folderId, "Report");
        var notesId = await DocumentWithContentAsync(owner, folderId, "Notes");
        return new Setup(tenantId, owner, admin, repoId, folderId, reportId, notesId);
    }

    private static async Task<Guid> DocumentWithContentAsync(HttpClient client, Guid parentId, string name)
    {
        var id = (await TestJson.Post(client, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(client, $"/api/documents/{id}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"{name}-{Guid.NewGuid():N}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(client, $"/api/documents/{id}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return id;
    }

    private static async Task<ZipArchive> ZipAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }

    private static async Task<string> OfflineHrefAsync(HttpClient client, Guid documentId) =>
        (await TestJson.Get(client, $"/api/documents/{documentId}")).GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "offlineExport").GetProperty("href").GetString()!;

    private static List<JsonElement> Lines(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open());
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    private static JsonElement Manifest(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    private static HashSet<Guid> Ids(ZipArchive archive, string path, string property = "id") =>
        [.. Lines(archive, path).Select(l => l.GetProperty(property).GetGuid())];

    [Fact]
    public async Task For_a_caller_who_may_read_everything_it_is_the_regular_export_plus_the_folder_chain()
    {
        var s = await SetUpAsync();

        using var regular = await ZipAsync(s.Admin, $"/api/documents/{s.FolderId}/export?versions=active");
        using var offline = await ZipAsync(s.Admin, await OfflineHrefAsync(s.Admin, s.FolderId));

        Assert.True(Manifest(offline).GetProperty("offline").GetBoolean());
        Assert.Equal("current", Manifest(offline).GetProperty("filters").GetProperty("versions").GetString());
        Assert.Equal(Ids(regular, "tree/documents.jsonl").Append(s.RepoId).ToHashSet(), Ids(offline, "tree/documents.jsonl"));
        Assert.Equal(Ids(regular, "tree/versions.jsonl"), Ids(offline, "tree/versions.jsonl"));
        Assert.Equal(
            regular.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).Select(e => e.FullName).Order(),
            offline.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.Ordinal)).Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task A_viewer_gets_only_what_they_may_read_and_a_single_document_comes_with_its_folders()
    {
        var s = await SetUpAsync();
        await TestJson.Put(s.Owner, $"/api/documents/{s.NotesId}/acl-entries/inheritance", new { breaksInheritance = true });
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@e2e.local";
        var viewerId = await _factory.SeedUserAsync(s.TenantId, viewerEmail, "ViewerPw123!", "Viewer");
        await TestJson.Put(s.Owner, $"/api/documents/{s.RepoId}/acl-entries/users/{viewerId}", new { canSee = true, canReadContent = true });
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(viewerEmail, "ViewerPw123!"));

        using var folder = await ZipAsync(viewer, await OfflineHrefAsync(viewer, s.RepoId));
        var documents = Ids(folder, "tree/documents.jsonl");
        Assert.Contains(s.ReportId, documents);
        Assert.DoesNotContain(s.NotesId, documents);   // never named to a caller who cannot see it

        // One document alone, as the phone keeps an opened one: with the folders above it, metadata only.
        using var single = await ZipAsync(viewer, await OfflineHrefAsync(viewer, s.ReportId));
        Assert.Equal(new HashSet<Guid> { s.RepoId, s.FolderId, s.ReportId }, Ids(single, "tree/documents.jsonl"));
        Assert.Equal([s.ReportId], Lines(single, "tree/versions.jsonl").Select(v => v.GetProperty("documentId").GetGuid()));
        var parents = Lines(single, "tree/documents.jsonl").ToDictionary(d => d.GetProperty("id").GetGuid(), d => d.GetProperty("parentId"));
        Assert.Equal(s.FolderId, parents[s.ReportId].GetGuid());
        Assert.Equal(s.RepoId, parents[s.FolderId].GetGuid());
    }

    [Fact]
    public async Task Each_offline_export_is_recorded_and_a_tenant_that_forbids_the_copy_refuses_it()
    {
        var s = await SetUpAsync();
        var href = await OfflineHrefAsync(s.Admin, s.ReportId);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, href))).StatusCode);
        using (await ZipAsync(s.Admin, href))
        {
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == s.TenantId && e.Action == "Document.OfflineExported" && e.TargetId == s.ReportId));
            var tenant = await db.Tenants.SingleAsync(t => t.Id == s.TenantId);
            tenant.OfflineCacheAllowed = false;
            await db.SaveChangesAsync();
        }

        var refused = await s.Admin.GetAsync(href);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("OFFLINE_EXPORT_NOT_ALLOWED", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
