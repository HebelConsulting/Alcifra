using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.ApiClient.Offline;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.IntegrationTests;

// The offline copy follows references (core ADR 0959, #1881): the offline export brings in a referenced document, a
// referenced folder's whole subtree, and what references inside those reach, each target once; a target the caller
// may not see is not brought in and its reference row says so without naming it. The repository export does not
// change: it is a restorable snapshot of its subtree with in-scope references only.
public partial class RepositoryExporterTests
{
    // Personal → My documents (the anchor) → Local, and Personal → Archive holding an in-scope reference to Local;
    // Contracts → X, Y/ → (DocY, Z/ → DocZ), Secret. References: My documents → X, Y and Secret; Y → X (a target met
    // twice); Z → My documents and Z → Y (cycles).
    private sealed record ReferenceSeed(Guid Personal, Guid MyDocuments, Guid Archive, Guid Local, Guid Contracts, Guid X, Guid Y, Guid DocY, Guid Z, Guid DocZ, Guid Secret,
        Guid UserId, Guid ReferenceToX, Guid YReferenceToX);

    private async Task<ReferenceSeed> SeedReferencesAsync(SqliteConnection connection, FakeStorage storage)
    {
        using var context = CreateContext(connection);
        var userId = Guid.NewGuid();
        context.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = _tenantId, Email = "anna@acme.test", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });

        Document Doc(string name, Guid? parent) => new() { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = parent, Name = name, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        var personal = Doc("Personal", null);
        var myDocuments = Doc("My documents", personal.Id);
        var archive = Doc("Archive", personal.Id);
        var local = Doc("Local", myDocuments.Id);
        var contracts = Doc("Contracts", null);
        var x = Doc("X", contracts.Id);
        var y = Doc("Y", contracts.Id);
        var docY = Doc("DocY", y.Id);
        var z = Doc("Z", y.Id);
        var docZ = Doc("DocZ", z.Id);
        var secret = Doc("Secret", contracts.Id);
        context.Documents.AddRange(personal, myDocuments, archive, local, contracts, x, y, docY, z, docZ, secret);
        foreach (var document in new[] { local, x, docY, docZ, secret })
        {
            AddVersion(context, storage, document.Id, userId, document.Name);
        }

        DocumentReference Reference(Document folder, Document target) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentFolderId = folder.Id,
            TargetDocumentId = target.Id,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var toX = Reference(myDocuments, x);
        var yToX = Reference(y, x);
        context.DocumentReferences.AddRange(toX, Reference(myDocuments, y), Reference(myDocuments, secret), yToX,
            Reference(z, myDocuments), Reference(z, y), Reference(archive, local));
        await context.SaveChangesAsync();
        return new ReferenceSeed(personal.Id, myDocuments.Id, archive.Id, local.Id, contracts.Id, x.Id, y.Id, docY.Id, z.Id, docZ.Id, secret.Id, userId, toX.Id, yToX.Id);
    }

    private void AddVersion(Infrastructure.Persistence.SimplArchiveDbContext context, FakeStorage storage, Guid documentId, Guid userId, string name)
    {
        var bytes = Encoding.UTF8.GetBytes($"{name}-bytes");
        var key = $"k/{documentId:N}.txt";
        storage.Objects[key] = bytes;
        context.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            ObjectKey = key,
            DocumentDate = new DateOnly(2026, 10, 1),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static readonly EffectiveRights Everything = new(true, true, true, true, true, true, true, true, true);
    private static readonly EffectiveRights Nothing = new(false, false, false, false, false, false, false, false, false);

    // The caller may read everything except what is denied.
    private static OfflineExportAudience Caller(Guid userId, params Guid[] denied) => new(userId, (ids, _) =>
        Task.FromResult<IReadOnlyDictionary<Guid, EffectiveRights>>(ids.Distinct().ToDictionary(id => id, id => denied.Contains(id) ? Nothing : Everything)));

    private async Task<byte[]> OfflineExportAsync(SqliteConnection connection, FakeStorage storage, Guid rootId, OfflineExportAudience audience)
    {
        using var context = CreateContext(connection);
        var output = new MemoryStream();
        await new RepositoryExporter(context, storage).ExportAsync(rootId, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.CurrentForCaller, null),
            false, output, audience, CancellationToken.None);
        return output.ToArray();
    }

    private static List<JsonElement> JsonLines(Dictionary<string, string> entries, string path) =>
        entries.TryGetValue(path, out var text) ? [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())] : [];

    private static async Task<(SqliteConnection Connection, FakeStorage Storage)> DatabaseAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        return (connection, new FakeStorage());
    }

    [Fact]
    public async Task The_offline_export_brings_referenced_documents_and_folder_subtrees_each_once()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);

        var entries = await ReadArchiveAsync(await OfflineExportAsync(connection, storage, seed.MyDocuments, Caller(seed.UserId, seed.Secret)));

        // Each document once, though Z references back into the anchor and into Y, and Y and My documents both reach X.
        var documents = JsonLines(entries, "tree/documents.jsonl").Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(documents.Count, documents.Distinct().Count());
        Assert.Equal(new HashSet<Guid> { seed.Personal, seed.MyDocuments, seed.Local, seed.Contracts, seed.X, seed.Y, seed.DocY, seed.Z, seed.DocZ }, documents.ToHashSet());

        // Their content, as any exported document's; the chain above a target (Contracts) is metadata only.
        Assert.Equal(new HashSet<Guid> { seed.Local, seed.X, seed.DocY, seed.DocZ },
            JsonLines(entries, "tree/versions.jsonl").Select(v => v.GetProperty("documentId").GetGuid()).ToHashSet());
        Assert.Equal("X-bytes", entries[$"blobs/{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("X-bytes")))}"]);
        Assert.Equal(new HashSet<Guid> { seed.X, seed.Y, seed.DocY, seed.Z, seed.DocZ },
            JsonLines(entries, "tree/referenced.jsonl").Select(r => r.GetProperty("documentId").GetGuid()).ToHashSet());

        // Every reference filed in what the export holds is listed; one into the chain (Archive) is not.
        var references = JsonLines(entries, "tree/references.jsonl");
        Assert.Equal(6, references.Count);
        var toX = references.Single(r => r.GetProperty("id").GetGuid() == seed.ReferenceToX);
        Assert.Equal((true, "X", false), (toX.GetProperty("targetAccessible").GetBoolean(), toX.GetProperty("targetName").GetString(), toX.GetProperty("targetIsFolder").GetBoolean()));
        Assert.True(references.Single(r => r.GetProperty("targetDocumentId").GetGuid() == seed.Y && r.GetProperty("parentFolderId").GetGuid() == seed.MyDocuments)
            .GetProperty("targetIsFolder").GetBoolean());
    }

    [Fact]
    public async Task A_target_the_caller_may_not_see_is_not_brought_in_and_its_reference_names_nothing()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);

        // Secret is invisible; so is Z, beneath the visible folder Y: it is left out with its subtree.
        var entries = await ReadArchiveAsync(await OfflineExportAsync(connection, storage, seed.MyDocuments, Caller(seed.UserId, seed.Secret, seed.Z)));

        var documents = JsonLines(entries, "tree/documents.jsonl").Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(seed.Secret, documents);
        Assert.DoesNotContain(seed.Z, documents);
        Assert.DoesNotContain(seed.DocZ, documents);
        Assert.Contains(seed.DocY, documents);

        var toSecret = JsonLines(entries, "tree/references.jsonl").Single(r => r.GetProperty("targetDocumentId").GetGuid() == seed.Secret);
        Assert.False(toSecret.GetProperty("targetAccessible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, toSecret.GetProperty("targetName").ValueKind);
        Assert.DoesNotContain("Secret", entries["tree/references.jsonl"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repository_export_is_unchanged_by_references_out_of_its_scope()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);

        var entries = await ReadArchiveAsync(await ExportAsync(connection, storage, seed.Personal, AllVersions));

        // Its own subtree only: no target from Contracts, no file of the offline copy's.
        Assert.Equal(new HashSet<Guid> { seed.Personal, seed.MyDocuments, seed.Local },
            JsonLines(entries, "tree/documents.jsonl").Select(d => d.GetProperty("id").GetGuid()).ToHashSet());
        Assert.False(entries.ContainsKey("tree/referenced.jsonl"));

        // In-scope references only, and in the shape a repository import reads: Archive → Local, though Archive is pruned
        // (it holds no document), so none survives here; My documents' three point outside.
        Assert.Empty(JsonLines(entries, "tree/references.jsonl"));
        Assert.Equal(0, JsonDocument.Parse(entries["manifest.json"]).RootElement.GetProperty("counts").GetProperty("references").GetInt32());
    }

    [Fact]
    public async Task The_repository_export_keeps_an_in_scope_reference_in_its_own_shape()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);
        using (var context = CreateContext(connection))
        {
            // Give Archive a document so it survives the export's pruning, and its reference to Local with it.
            AddVersion(context, storage, (await AddDocumentAsync(context, "Kept", seed.Archive, seed.UserId)).Id, seed.UserId, "Kept");
            await context.SaveChangesAsync();
        }

        var entries = await ReadArchiveAsync(await ExportAsync(connection, storage, seed.Personal, AllVersions));

        var reference = Assert.Single(JsonLines(entries, "tree/references.jsonl"));
        Assert.Equal(seed.Local, reference.GetProperty("targetDocumentId").GetGuid());
        Assert.Equal(["createdAt", "createdByServiceAccountId", "createdByUserId", "id", "parentFolderId", "targetDocumentId"],
            reference.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private async Task<Document> AddDocumentAsync(Infrastructure.Persistence.SimplArchiveDbContext context, string name, Guid parentId, Guid userId)
    {
        var document = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = parentId, Name = name, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    [Fact]
    public async Task The_offline_export_follows_at_most_its_limit_of_targets()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);
        using (var context = CreateContext(connection))
        {
            for (var i = 0; i < OfflineReferenceFollowerLimit; i++)
            {
                var target = new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = seed.Contracts, Name = $"Many {i}", CreatedByUserId = seed.UserId, CreatedAt = DateTimeOffset.UtcNow };
                context.Documents.Add(target);
                context.DocumentReferences.Add(new DocumentReference
                {
                    Id = Guid.NewGuid(),
                    TenantId = _tenantId,
                    ParentFolderId = seed.MyDocuments,
                    TargetDocumentId = target.Id,
                    CreatedByUserId = seed.UserId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await context.SaveChangesAsync();
        }

        var entries = await ReadArchiveAsync(await OfflineExportAsync(connection, storage, seed.MyDocuments, Caller(seed.UserId, seed.Secret)));

        // Every reference in the anchor is listed; only the limit's worth of their targets is brought in (502 are visible).
        var listed = JsonLines(entries, "tree/references.jsonl").Where(r => r.GetProperty("parentFolderId").GetGuid() == seed.MyDocuments)
            .Select(r => r.GetProperty("targetDocumentId").GetGuid()).ToHashSet();
        Assert.Equal(OfflineReferenceFollowerLimit + 3, listed.Count);
        var targets = JsonLines(entries, "tree/referenced.jsonl").Select(r => r.GetProperty("documentId").GetGuid()).ToHashSet();
        Assert.Equal(OfflineReferenceFollowerLimit, targets.Count(listed.Contains));
    }

    private const int OfflineReferenceFollowerLimit = 500;

    [Fact]
    public void The_follower_limit_is_the_one_this_test_suite_states() =>
        Assert.Equal(OfflineReferenceFollowerLimit, OfflineReferenceFollower.MaxFollowedTargets);

    [Fact]
    public async Task A_copy_drops_what_a_reference_brought_in_once_no_export_brings_or_holds_it()
    {
        var (connection, storage) = await DatabaseAsync();
        using var _ = connection;
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var seed = await SeedReferencesAsync(connection, storage);

        var directory = Directory.CreateTempSubdirectory("offline-references-").FullName;
        try
        {
            var copy = await CopyAsync(directory);
            var merged = await copy.MergeAsync(new ZipArchiveStorage(await OfflineExportAsync(connection, storage, seed.MyDocuments, Caller(seed.UserId, seed.Secret))));
            Assert.Contains(seed.X, merged);
            Assert.True(copy.IsByReferenceOnly(seed.X));
            Assert.True(copy.IsByReferenceOnly(seed.DocZ));
            Assert.False(copy.IsByReferenceOnly(seed.Local));

            // DocY was also opened on its own: that export holds it, so it is no longer held by reference only.
            await copy.MergeAsync(new ZipArchiveStorage(await OfflineExportAsync(connection, storage, seed.DocY, Caller(seed.UserId, seed.Secret))));
            Assert.False(copy.IsByReferenceOnly(seed.DocY));
            copy.Flush();

            // The references to X and Y go: X and Y's subtree leave the copy, except DocY, which its own export holds.
            using (var context = CreateContext(connection))
            {
                context.DocumentReferences.RemoveRange(context.DocumentReferences.Where(r => r.TargetDocumentId == seed.X || r.TargetDocumentId == seed.Y));
                await context.SaveChangesAsync();
            }

            var later = await CopyAsync(directory);   // a later run reads the bookkeeping back
            await later.MergeAsync(new ZipArchiveStorage(await OfflineExportAsync(connection, storage, seed.MyDocuments, Caller(seed.UserId, seed.Secret))));
            later.Flush();

            Assert.False(later.Contains(seed.X));
            Assert.False(later.Contains(seed.DocZ));
            Assert.True(later.Contains(seed.DocY));
            Assert.True(later.Contains(seed.Local));

            // And the responder lists what is left: My documents' reference to Secret, saying it is not the user's to see.
            var archive = await OfflineArchive.OpenAsync(new DirectoryArchiveStorage(directory));
            var reference = Assert.Single(archive.References(seed.MyDocuments));
            Assert.Equal((seed.Secret, (bool?)false), (reference.TargetId, reference.TargetAccessible));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
