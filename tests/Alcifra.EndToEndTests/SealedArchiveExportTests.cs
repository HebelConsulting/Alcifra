using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Api.Errors.Exceptions.OfflineArchives;
using Alcifra.Api.OfflineArchives;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;
using Alcifra.SealedArchive;
using Alcifra.SealedArchive.Errors;

namespace Alcifra.EndToEndTests;

// A road copy end to end (ADRs 0960/0961, #1694/#1695): written by the server over real Postgres and object storage,
// read back by the shared client's responder. It opens with the recovery key alone; every version travels; the sync
// preparation and opted-in previews ride along; and a tampered blob, a tampered cover and a foreign signature are each
// refused by name. Each test makes its own tenant and documents (ADR 0884).
//
// No HTTP door yet: the export job (#1696) is the door, so the writer is resolved in a tenant scope as the job will.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SealedArchiveExportTests
{
    private const string ApiRoot = "https://archive.e2e.local/api";

    private readonly E2EApiFactory _factory;

    public SealedArchiveExportTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Setup(Guid TenantId, Guid ExporterId, Guid RepoId, Guid ReportId, X509Certificate2 ExporterKey, IReadOnlyList<string> ReportContents);

    private sealed class FixedRecovery(string? pem) : IRecoveryCertificateSource
    {
        public Task<string?> CurrentPemAsync(CancellationToken cancellationToken) => Task.FromResult(pem);
    }

    // Stands in for RenditionService: every version is displayed through a rendition beside it ("…/content.preview.pdf").
    private sealed class RenditionBeside : IDocumentPreviewService
    {
        public Task<DocumentPreview?> GetPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default, bool sourceMayHaveChanged = false) =>
            Task.FromResult<DocumentPreview?>(null);

        public Task<string> GetDisplayObjectKeyAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(Rendition(objectKey));

        public Task<PreviewPages?> GetPreviewPagesAsync(string objectKey, TimeSpan expiry, string? fileName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<PreviewPages?>(null);

        public Task<IReadOnlyList<string>> GetDisplayObjectKeysAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([Rendition(objectKey)]);

        public static string Rendition(string objectKey) => $"{objectKey}.preview.pdf";
    }

    private static X509Certificate2 Rsa(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    // A software key opening its envelope, as a card would (RSA key transport).
    private static Func<byte[], CancellationToken, Task<byte[]>> OpenerFor(X509Certificate2 holder) => (envelope, _) =>
    {
        var cms = new EnvelopedCms();
        cms.Decode(envelope);
        cms.Decrypt(new X509Certificate2Collection(holder));
        return Task.FromResult(cms.ContentInfo.Content);
    };

    private async Task<Setup> SetUpAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var exporterId = await _factory.SeedUserAsync(tenantId, $"road-{Guid.NewGuid():N}@e2e.local", "road-1234", "Rita Road", isTenantAdmin: true);
        var exporterKey = Rsa("Rita Road");
        await using (var scope = Scope(tenantId, userId: null))
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            (await db.Users.SingleAsync(u => u.Id == exporterId)).SmimeCertificatePem = exporterKey.ExportCertificatePem();
            await db.SaveChangesAsync();
        }

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Road {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Contracts" })).GetProperty("id").GetGuid();
        var reportId = (await TestJson.Post(owner, $"/api/documents/{folderId}/children", new { name = "Report" })).GetProperty("id").GetGuid();
        var contents = new List<string>();
        for (var i = 1; i <= 2; i++)
        {
            var content = $"Report v{i} {Guid.NewGuid():N}";
            var created = await TestJson.Post(owner, $"/api/documents/{reportId}/versions", new { fileExtension = ".txt" });
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(content)))).EnsureSuccessStatusCode();
            }

            await TestJson.Put(owner, $"/api/documents/{reportId}/versions/{created.GetProperty("id").GetGuid()}", new { });
            contents.Add(content);
        }

        return new Setup(tenantId, exporterId, repoId, reportId, exporterKey, contents);
    }

    private AsyncServiceScope Scope(Guid tenantId, Guid? userId)
    {
        var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().UserId = userId;
        return scope;
    }

    private async Task<byte[]> ExportAsync(Setup setup, string? recoveryPem, bool previews = false, IReadOnlyList<Guid>? members = null)
    {
        await using var scope = Scope(setup.TenantId, setup.ExporterId);
        object[] overrides = previews ? [new FixedRecovery(recoveryPem), new RenditionBeside()] : [new FixedRecovery(recoveryPem)];
        var exporter = ActivatorUtilities.CreateInstance<SealedArchiveExporter>(scope.ServiceProvider, overrides);
        using var output = new MemoryStream();
        await exporter.ExportAsync(new SealedArchiveRequest(setup.RepoId, null, previews, members ?? [], [], ApiRoot), output, CancellationToken.None);
        return output.ToArray();
    }

    private IReadOnlyList<X509Certificate2> PublishedKeys => _factory.Services.GetRequiredService<ArchiveSigningKey>().History;

    [Fact]
    public async Task It_opens_with_the_recovery_key_alone_and_carries_every_version_and_the_sync_preparation()
    {
        var setup = await SetUpAsync();
        var recovery = Rsa("Road recovery");
        var archive = await ExportAsync(setup, recovery.ExportCertificatePem());

        // The recovery key alone: the exporter's card is not involved.
        using var opened = await SealedArchiveReader.OpenAsync(new MemoryStream(archive), PublishedKeys, OpenerFor(recovery));
        Assert.Equal([ArchiveRecipientKind.Exporter, ArchiveRecipientKind.Recovery], opened.Cover.Recipients.Select(r => r.Kind));
        Assert.Equal((ApiRoot, setup.TenantId), (opened.Cover.Source.ApiRoot, opened.Cover.Source.TenantId));

        // Every version travels, each version's bytes under its hash (the export layout the responder reads; that it
        // reads an opened archive is SealedArchiveTests'). Not through the shared client here: referencing it from this
        // project would shadow the private tools' own ApiClient type (the tools build against this project).
        var versions = Encoding.UTF8.GetString(opened.Read("tree/versions.jsonl")!).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).Where(v => v.GetProperty("documentId").GetGuid() == setup.ReportId)
            .OrderBy(v => v.GetProperty("versionNumber").GetInt32()).ToList();
        Assert.Equal([1, 2], versions.Select(v => v.GetProperty("versionNumber").GetInt32()));
        foreach (var (version, content) in versions.Zip(setup.ReportContents))
        {
            Assert.Equal(content, Encoding.UTF8.GetString(opened.Read($"blobs/{version.GetProperty("blobRef").GetString()}")!));
        }

        // Sync preparation: the source, each document's tag as exported, and an empty journal.
        var source = JsonDocument.Parse(opened.Read("sync/source.json")!).RootElement;
        Assert.Equal(ApiRoot, source.GetProperty("apiRoot").GetString());
        var token = await TokenAsync(setup.TenantId, setup.ReportId);
        var tag = Encoding.UTF8.GetString(opened.Read("sync/tags.jsonl")!).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).Single(r => r.GetProperty("documentId").GetGuid() == setup.ReportId);
        Assert.Equal($"\"{token}\"", tag.GetProperty("etag").GetString());
        Assert.Equal(2, tag.GetProperty("versions").GetArrayLength());
        Assert.Empty(opened.Read("sync/journal.jsonl")!);

        // And the exporter's own key opens it too.
        using var byExporter = await SealedArchiveReader.OpenAsync(new MemoryStream(archive), PublishedKeys, OpenerFor(setup.ExporterKey));
        Assert.NotNull(byExporter.Read("manifest.json"));
    }

    [Fact]
    public async Task Previews_travel_when_opted_in()
    {
        var setup = await SetUpAsync();
        await using (var scope = Scope(setup.TenantId, null))
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
            foreach (var key in await db.DocumentVersions.Where(v => v.DocumentId == setup.ReportId).Select(v => v.ObjectKey).ToListAsync())
            {
                await storage.PutObjectAsync(RenditionBeside.Rendition(key), new MemoryStream(Encoding.UTF8.GetBytes($"preview of {key}")), "application/pdf");
            }
        }

        var recovery = Rsa("Road recovery");
        using var opened = await SealedArchiveReader.OpenAsync(new MemoryStream(await ExportAsync(setup, recovery.ExportCertificatePem(), previews: true)),
            PublishedKeys, OpenerFor(recovery));

        var rows = Encoding.UTF8.GetString(opened.Read("tree/previews.jsonl")!).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("preview of ", Encoding.UTF8.GetString(opened.Read($"blobs/{r.GetProperty("blobRef").GetString()}")!), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_recovery_certificate_the_export_is_refused_by_name()
    {
        var setup = await SetUpAsync();

        await Assert.ThrowsAsync<RecoveryCertificateRequiredException>(() => ExportAsync(setup, recoveryPem: null));

        // ...and that is what the installation answers for a tenant whose administrator has set none (the real source).
        await using var scope = Scope(setup.TenantId, setup.ExporterId);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<RecoveryCertificateRequiredException>(() => scope.ServiceProvider.GetRequiredService<SealedArchiveExporter>()
            .ExportAsync(new SealedArchiveRequest(setup.RepoId, null, false, [], [], ApiRoot), output, CancellationToken.None));
    }

    [Fact]
    public async Task A_member_without_a_certificate_is_refused_by_name_rather_than_left_out()
    {
        var setup = await SetUpAsync();
        var member = await _factory.SeedUserAsync(setup.TenantId, $"nocert-{Guid.NewGuid():N}@e2e.local", "nocert-1234", "Nora NoCert");

        var refusal = await Assert.ThrowsAsync<ArchiveRecipientWithoutCertificateException>(() =>
            ExportAsync(setup, Rsa("Road recovery").ExportCertificatePem(), members: [member]));
        Assert.Contains("Nora NoCert", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_blob_a_tampered_cover_and_a_foreign_signature_are_each_refused_by_name()
    {
        var setup = await SetUpAsync();
        var recovery = Rsa("Road recovery");
        var archive = await ExportAsync(setup, recovery.ExportCertificatePem());

        // A tampered blob: one sealed payload entry changed.
        var blobEntry = Entries(archive).First(e => e.StartsWith("payload/", StringComparison.Ordinal) && e != SealedArchiveLayout.Contents);
        using (var opened = await SealedArchiveReader.OpenAsync(new MemoryStream(Rewrite(archive, blobEntry, Flip)), PublishedKeys, OpenerFor(recovery)))
        {
            Assert.Throws<ArchiveBlobTamperedException>(() => opened.Paths.Select(opened.Read).ToList());
        }

        // A tampered cover: the name changed after signing.
        var renamed = Rewrite(archive, SealedArchiveLayout.Cover, bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("Road ", "Rood ", StringComparison.Ordinal)));
        Assert.Throws<ArchiveCoverTamperedException>(() => SealedArchiveReader.ReadCover(new MemoryStream(renamed), PublishedKeys));

        // A foreign signature: a valid one, by a key this installation never published.
        Assert.Throws<ArchiveForeignSignatureException>(() => SealedArchiveReader.ReadCover(new MemoryStream(archive), [Rsa("Another installation")]));
    }

    private async Task<Guid> TokenAsync(Guid tenantId, Guid documentId)
    {
        await using var scope = Scope(tenantId, null);
        return await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Documents.Where(d => d.Id == documentId).Select(d => d.ConcurrencyToken).SingleAsync();
    }

    private static List<string> Entries(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive));
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private static byte[] Rewrite(byte[] archive, string entry, Func<byte[], byte[]> change)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(archive)))
        {
            foreach (var e in zip.Entries)
            {
                using var stream = e.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                entries.Add((e.FullName, e.FullName == entry ? change(buffer.ToArray()) : buffer.ToArray()));
            }
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    private static byte[] Flip(byte[] bytes)
    {
        var copy = bytes.ToArray();
        copy[^1] ^= 0x01;
        return copy;
    }
}
