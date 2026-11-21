using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.Domain.Masks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

/// <summary>
/// One key factory for document content (#2304, ABI 1.18/ADR 0937): a mask declaring a content segment keeps its bytes
/// under it whichever door wrote them, a mask change into or out of such a mask MOVES the existing versions, and the
/// read-only storage check counts what history left outside.
/// </summary>
/// <remarks>
/// Found by reading the code, not reproduced: WebDAV writes, the importer and the facade built the archive key
/// themselves, so a segmented mask's content could land in the year folders — invisible until a module's
/// delete-by-prefix or a sealed mask's storage door looked for it.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SegmentedContentKeysTests(E2EApiFactory factory)
{
    private const string Segment = "test-segment";

    [Fact]
    public async Task Every_door_files_a_segmented_mask_under_its_segment_and_a_mask_change_moves_the_versions()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var segmented = Alcifra.TestModule.TestModule.SegmentedMaskId;

            // THE CLIENT'S VERSION ENDPOINT: masks the document first, then obviously the version lands in the segment.
            var documentId = await CreateAsync(rig, "Client filed");
            await TestJson.Put(rig.Admin, $"/api/documents/{documentId}/mask", new { maskId = segmented });
            await UploadVersionAsync(rig.Admin, documentId, ".pdf", "first");
            Assert.Contains($"/{Segment}/", DownloadUrl(await CurrentVersionAsync(rig.Admin, documentId)), StringComparison.Ordinal);

            // THE WEBDAV DOOR — the hole the issue names. A mounted editor writes over the file (a working copy in the
            // check-out stash, ADR 0562), and the CHECK-IN files the new version; that key is built without a segment,
            // so the finalizer is what must put it back under the mask's segment. Both acts are the same user's: only
            // the lock holder may check in.
            var davPassword = (await TestJson.Post(rig.Admin, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
            var repoName = (await TestJson.Get(rig.Admin, $"/api/documents/{rig.RepoId}")).GetProperty("name").GetString()!;
            var documentName = (await TestJson.Get(rig.Admin, $"/api/documents/{documentId}")).GetProperty("name").GetString()!;
            using var dav = factory.CreateClient();
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/webdav/{Uri.EscapeDataString(repoName)}/{Uri.EscapeDataString(documentName)}.pdf")
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("appended over webdav")),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rig.AdminEmail}:{davPassword}")));
            (await dav.SendAsync(put)).EnsureSuccessStatusCode();

            // 204, no body — TestJson reads JSON, so the check-in's status is asserted directly.
            using var checkin = await rig.Admin.PostAsJsonAsync($"/api/documents/{documentId}/checkout/checkin", new { });
            Assert.True(checkin.IsSuccessStatusCode, $"checkin: {checkin.StatusCode} {await checkin.Content.ReadAsStringAsync()}");

            var current = await CurrentVersionAsync(rig.Admin, documentId);
            Assert.Contains($"/{Segment}/", DownloadUrl(current), StringComparison.Ordinal);
            Assert.Equal("appended over webdav", Encoding.ASCII.GetString(await ReadAsync(current)));

            // THE MASK CHANGE — the second hole: setting the mask after filing MOVES the existing versions, and takes
            // them back when the mask leaves again. The content is unchanged throughout.
            var plainId = await CreateAsync(rig, "Remasked");
            await UploadVersionAsync(rig.Admin, plainId, ".pdf", "plain bytes");
            var archiveUrl = DownloadUrl(await CurrentVersionAsync(rig.Admin, plainId));
            Assert.DoesNotContain($"/{Segment}/", archiveUrl, StringComparison.Ordinal);
            Assert.Contains("/2026/", archiveUrl, StringComparison.Ordinal);

            await TestJson.Put(rig.Admin, $"/api/documents/{plainId}/mask", new { maskId = segmented });
            var moved = await CurrentVersionAsync(rig.Admin, plainId);
            Assert.Contains($"/{Segment}/", DownloadUrl(moved), StringComparison.Ordinal);
            Assert.Equal("plain bytes", Encoding.ASCII.GetString(await ReadAsync(moved)));

            await TestJson.Put(rig.Admin, $"/api/documents/{plainId}/mask", new { maskId = WellKnownMaskIds.BasicEntry });
            var back = await CurrentVersionAsync(rig.Admin, plainId);
            Assert.DoesNotContain($"/{Segment}/", DownloadUrl(back), StringComparison.Ordinal);
            Assert.Contains("/2026/", DownloadUrl(back), StringComparison.Ordinal);
            Assert.Equal("plain bytes", Encoding.ASCII.GetString(await ReadAsync(back)));

            // THE IMPORTER — the third door the issue names, CHECKED rather than assumed. An import never produces a
            // document whose mask declares a content segment: the archive's masks are COPIED (a custom mask per archive
            // mask, and a copy carries no segment), and a document keeps a core-owned mask only where it already wore
            // it. The imported copy therefore belongs in the archive layout — the plain key its writer builds is the
            // correct key — and this asserts exactly that, so a future mapper that starts preserving a module's mask
            // fails here and must come with the re-housing.
            await factory.GrantTenantAdminAsync(rig.AdminEmail);
            var zip = await (await rig.Admin.GetAsync($"/api/documents/{rig.RepoId}/export?versions=all")).Content.ReadAsByteArrayAsync();
            JsonElement imported;
            using (var form = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(zip);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(file, "file", "export.zip");
                var response = await rig.Admin.PostAsync("/api/repositories/imports", form);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new Xunit.Sdk.XunitException($"import: {(int)response.StatusCode} {body}");
                }

                imported = JsonSerializer.Deserialize<JsonElement>(body);
            }

            var importedRoot = imported.GetProperty("rootId").GetGuid();
            var importedDoc = (await TestJson.Get(rig.Admin, $"/api/documents/{importedRoot}/children"))
                .GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == documentName)
                .GetProperty("id").GetGuid();
            var importedVersion = await CurrentVersionAsync(rig.Admin, importedDoc);
            Assert.DoesNotContain($"/{Segment}/", DownloadUrl(importedVersion), StringComparison.Ordinal);
            Assert.Contains("/2026/", DownloadUrl(importedVersion), StringComparison.Ordinal);
            Assert.Equal("appended over webdav", Encoding.ASCII.GetString(await ReadAsync(importedVersion)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    [Fact]
    public async Task The_storage_check_counts_segmented_content_that_sits_outside_its_segment()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var segmented = Alcifra.TestModule.TestModule.SegmentedMaskId;

            var documentId = await CreateAsync(rig, "Checked");
            await TestJson.Put(rig.Owner, $"/api/documents/{documentId}/mask", new { maskId = segmented });
            var versionId = await UploadVersionAsync(rig.Owner, documentId, ".pdf", "checked");

            var (clientId, secret) = await factory.SeedPlatformAdministratorAsync();
            using var platform = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));

            // Clean: the one version sits in its segment.
            var clean = await SegmentOfAsync(platform, rig.TenantId);
            Assert.Equal(1, clean.GetProperty("versions").GetInt32());
            Assert.Equal(0, clean.GetProperty("outOfSegment").GetInt32());

            // HISTORY, simulated: a version filed by a writer of the years before the one key factory sits in the
            // archive's year folders while its document wears the segmented mask. Rewritten in the database directly —
            // this is the state an installation upgraded from an older build is in, and what the check exists for.
            var archiveKey = $"tenants/{rig.TenantId:D}/2026/{Guid.NewGuid():D}/{versionId:D}.pdf";
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
                var version = await db.DocumentVersions.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(v => v.Id == versionId);
                version.ObjectKey = archiveKey;
                await db.SaveChangesAsync();
            }

            var flagged = await SegmentOfAsync(platform, rig.TenantId);
            Assert.Equal(1, flagged.GetProperty("versions").GetInt32());
            Assert.Equal(0, flagged.GetProperty("inSegment").GetInt32());
            Assert.Equal(1, flagged.GetProperty("outOfSegment").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    private static async Task<Guid> CreateAsync(TestModuleRig.Rig rig, string name) =>
        (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = $"{name} {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

    private static async Task<Guid> UploadVersionAsync(HttpClient api, Guid documentId, string extension, string content)
    {
        var pending = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(pending.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(content))))
                .EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{documentId}/versions/{pending.GetProperty("id").GetGuid()}", new { });
        return pending.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CurrentVersionAsync(HttpClient api, Guid documentId)
    {
        var versions = await TestJson.Get(api, $"/api/documents/{documentId}/versions");
        return versions.GetProperty("versions").EnumerateArray()
            .OrderBy(v => v.GetProperty("versionNumber").GetInt32()).Last();
    }

    private static string DownloadUrl(JsonElement version) =>
        version.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == "download")
            .Select(l => l.GetProperty("href").GetString() ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;

    private static async Task<byte[]> ReadAsync(JsonElement version)
    {
        // A PLAIN client, deliberately: the authed one routes through TestServer and forwards even an absolute URL to
        // the app, so a presigned storage GET answered 404 there while it worked on the wire (#2304).
        using var http = new HttpClient();
        using var download = await http.GetAsync(DownloadUrl(version));
        download.EnsureSuccessStatusCode();
        return await download.Content.ReadAsByteArrayAsync();
    }

    // The segment's own count for this tenant, from the platform administrator's check (#2304).
    private static async Task<JsonElement> SegmentOfAsync(HttpClient platform, Guid tenantId)
    {
        var answer = await TestJson.Get(platform, $"/api/tenants/{tenantId}/storage-out-of-segment");
        return answer.GetProperty("segments").EnumerateArray().First(s => s.GetProperty("segment").GetString() == Segment);
    }
}
