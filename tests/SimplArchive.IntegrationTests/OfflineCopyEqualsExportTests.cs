using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.ApiClient.Offline;

namespace SimplArchive.IntegrationTests;

// The owner's "ultimate test" of the offline copy (#1759, 2026-10-14): a copy built by merging exports IS the export.
// The real exporter writes an archive; the shared client's merger makes a device copy of it; every file the
// responder reads must hold the same rows, and every blob the same bytes. Then the merge rule: an export is a full
// snapshot of its anchor's subtree, so what the server no longer has goes from the copy too.
public partial class RepositoryExporterTests
{
    private static Task<byte[]> Plain(string path, byte[] bytes, CancellationToken _) => Task.FromResult(bytes);

    private static async Task<ArchiveMerger> CopyAsync(string directory) =>
        await ArchiveMerger.OpenAsync(directory, "Offline copies", (_, bytes) => bytes, Plain);

    // A file's rows, each as canonical JSON, sorted: two files with the same rows in any order compare equal.
    private static async Task<List<string>> RowsAsync(IArchiveStorage storage, string path) =>
        await storage.ReadAsync(path) is { } bytes
            ? [.. System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.ToJsonString()).Order(StringComparer.Ordinal)]
            : [];

    private static async Task<List<string>> ElementsAsync(IArchiveStorage storage, string path) =>
        await storage.ReadAsync(path) is { } bytes && JsonNode.Parse(bytes) is JsonArray array
            ? [.. array.Select(e => e!.ToJsonString()).Order(StringComparer.Ordinal)]
            : [];

    private static async Task<List<string>> PeopleAsync(IArchiveStorage storage, string kind) =>
        JsonNode.Parse((await storage.ReadAsync("principals/principals.json"))!)![kind] is JsonArray array
            ? [.. array.Select(e => e!.ToJsonString()).Order(StringComparer.Ordinal)]
            : [];

    [Fact]
    public async Task A_copy_merged_from_an_export_holds_exactly_what_the_export_holds()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 1, 1));
        var export = new ZipArchiveStorage(await ExportAsync(connection, storage, seed.RepoId, AllVersions));

        var directory = Directory.CreateTempSubdirectory("offline-copy-").FullName;
        try
        {
            var copy = await CopyAsync(directory);
            await copy.MergeAsync(export);
            copy.Flush();
            var kept = new DirectoryArchiveStorage(directory);

            foreach (var path in new[] { "tree/documents.jsonl", "tree/versions.jsonl", "tree/index-data.jsonl", "tree/chat.jsonl", "tree/annotations.jsonl", "tree/references.jsonl" })
            {
                Assert.Equal(await RowsAsync(export, path), await RowsAsync(kept, path));
            }

            Assert.Equal(await ElementsAsync(export, "masks/masks.json"), await ElementsAsync(kept, "masks/masks.json"));
            Assert.Equal(await ElementsAsync(export, "labels/labels.json"), await ElementsAsync(kept, "labels/labels.json"));
            foreach (var kind in new[] { "users", "serviceAccounts", "groups", "memberships" })
            {
                Assert.Equal(await PeopleAsync(export, kind), await PeopleAsync(kept, kind));
            }
            foreach (var sha in new[] { seed.V1Sha, seed.V2Sha, seed.DocBSha })
            {
                Assert.Equal(await export.ReadAsync($"blobs/{sha}"), await kept.ReadAsync($"blobs/{sha}"));
            }

            // And the responder reads the copy as it reads the export.
            var fromCopy = await OfflineArchive.OpenAsync(kept);
            var fromExport = await OfflineArchive.OpenAsync(export);
            Assert.Equal(fromExport.AllDocuments().Select(d => (d.Id, d.ParentId, d.Name, fromExport.MaskName(d))).Order(),
                fromCopy.AllDocuments().Select(d => (d.Id, d.ParentId, d.Name, fromCopy.MaskName(d))).Order());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_later_export_of_the_same_folder_removes_what_the_server_no_longer_has()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 1, 1));

        var directory = Directory.CreateTempSubdirectory("offline-copy-").FullName;
        try
        {
            var copy = await CopyAsync(directory);
            await copy.MergeAsync(new ZipArchiveStorage(await ExportAsync(connection, storage, seed.RepoId, AllVersions)));
            copy.Flush();
            Assert.True(copy.Contains(seed.DocBId));

            using (var context = CreateContext(connection))
            {
                (await context.Documents.SingleAsync(d => d.Id == seed.DocBId)).DeletedAt = DateTimeOffset.UtcNow;
                await context.SaveChangesAsync();
            }

            var later = await CopyAsync(directory);   // a later run reads back what was kept
            await later.MergeAsync(new ZipArchiveStorage(await ExportAsync(connection, storage, seed.FolderId, AllVersions)));
            later.Flush();
            later.Prune();

            Assert.False(later.Contains(seed.DocBId));
            Assert.True(later.Contains(seed.DocAId));
            Assert.True(later.Contains(seed.RepoId));   // outside the folder's subtree: untouched
            Assert.Null(await new DirectoryArchiveStorage(directory).ReadAsync($"blobs/{seed.DocBSha}"));   // its content freed
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task One_documents_export_refreshes_that_document_and_leaves_its_siblings()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var storage = new FakeStorage();
        var seed = await SeedAsync(connection, storage, new DateOnly(2025, 1, 1));

        var directory = Directory.CreateTempSubdirectory("offline-copy-").FullName;
        try
        {
            var copy = await CopyAsync(directory);
            await copy.MergeAsync(new ZipArchiveStorage(await ExportAsync(connection, storage, seed.RepoId, AllVersions)));
            var refreshed = await copy.MergeAsync(new ZipArchiveStorage(await ExportAsync(connection, storage, seed.DocAId,
                new(null, null, null, null, Api.Documents.ExportVersionSelection.ActiveOnly, null))));
            copy.Flush();

            Assert.Equal(new HashSet<Guid> { seed.DocAId }, refreshed);
            Assert.True(copy.Contains(seed.DocBId));   // a sibling outside the anchor's subtree stays
            var versions = await RowsAsync(new DirectoryArchiveStorage(directory), "tree/versions.jsonl");
            Assert.DoesNotContain(versions, v => v.Contains(seed.V1Id.ToString(), StringComparison.Ordinal));   // DocA now holds the active version only
            Assert.Contains(versions, v => v.Contains(seed.V2Id.ToString(), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
