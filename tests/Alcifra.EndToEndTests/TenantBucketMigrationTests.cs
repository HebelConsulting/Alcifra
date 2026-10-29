using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Storage;

namespace Alcifra.EndToEndTests;

// The tenant buckets move from one prefix to another without downtime (ADR 1000): reads fall back to the legacy
// bucket, deletes reach both, a change to an object still only in the legacy bucket moves it first, and the batches
// `alcifra storage migrate` drives copy with metadata, retention and legal hold, then remove what no lock keeps.
//
// AN E2E TEST BECAUSE THE FIXTURE CANNOT BE FAKED HONESTLY: what a server-side copy carries (metadata yes, lock no)
// is the store's behaviour, and the whole design rests on it. The clients are built directly with two prefixes of
// their own, so nothing else in the suite sees these buckets.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TenantBucketMigrationTests
{
    private const string Legacy = "legacy";
    private const string Current = "current";

    private readonly E2EApiFactory _factory;

    public TenantBucketMigrationTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Reads_fall_back_to_the_legacy_bucket_and_deletes_reach_both()
    {
        var tenantId = Guid.NewGuid();
        var before = Client(Legacy, legacy: null);
        await before.EnsureTenantBucketAsync(tenantId);
        var kept = await PutAsync(before, tenantId, "kept", new Dictionary<string, string> { ["sa-test"] = "carried" });
        var purged = await PutAsync(before, tenantId, "purged");
        var held = await PutAsync(before, tenantId, "held");

        var during = Client(Current, Legacy);
        await during.EnsureTenantBucketAsync(tenantId);

        await using (var read = await during.GetObjectAsync(kept))
        {
            Assert.Equal("kept", await new StreamReader(read).ReadToEndAsync());
        }

        Assert.Equal("carried", (await during.GetObjectInfoAsync(kept)).Metadata["sa-test"]);
        Assert.True(await during.ExistsAsync(kept));
        Assert.Equal(4, await during.GetObjectSizeAsync(kept));
        Assert.Contains(await during.ListObjectsAsync(ObjectKeyPrefixes.Tenant(tenantId)), o => o.Key == kept);

        // A presign makes no request, so it decides by a HEAD: the address points at the bucket that holds the object.
        var preview = await during.GetPresignedPreviewUrlAsync(kept, TimeSpan.FromMinutes(1));
        using (var http = new HttpClient())
        {
            Assert.Equal("kept", await http.GetStringAsync(preview));
        }

        // "Absent" means absent from both: a claim still in the legacy bucket is held.
        Assert.False(await during.TryPutIfAbsentAsync(kept, new MemoryStream([1]), "application/octet-stream"));

        // Deleted from BOTH, or the next migration run brings a purged object back.
        await during.DeleteObjectForGoodAsync(purged);
        Assert.False(await during.ExistsAsync(purged));
        Assert.False(await RawExistsAsync(BucketOf(Legacy, tenantId), purged));

        // A change to an object found only in the legacy bucket moves it across first, so the write lands in the new one.
        await during.SetLegalHoldAsync(held, held: true);
        Assert.True(await RawExistsAsync(BucketOf(Current, tenantId), held));
        using var raw = _factory.CreateStorageClient();
        var hold = await raw.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = BucketOf(Current, tenantId), Key = held });
        Assert.Equal(ObjectLockLegalHoldStatus.On, hold.LegalHold.Status);
        await during.SetLegalHoldAsync(held, held: false);
    }

    [Fact]
    public async Task A_migration_copies_with_metadata_and_locks_removes_what_no_lock_keeps_and_runs_again_safely()
    {
        var tenantId = Guid.NewGuid();
        var before = Client(Legacy, legacy: null);
        await before.EnsureTenantBucketAsync(tenantId);
        var plain = await PutAsync(before, tenantId, "plain", new Dictionary<string, string> { ["sa-wrapped-dek"] = "stands-in" });
        var retained = await PutAsync(before, tenantId, "retained");
        var until = DateTimeOffset.UtcNow.AddDays(1);
        await before.SetRetentionAsync(retained, until, Alcifra.Domain.Tenants.WormLockMode.Governance);
        var onHold = await PutAsync(before, tenantId, "on hold");
        await before.SetLegalHoldAsync(onHold, held: true);
        var rewritten = await PutAsync(before, tenantId, "old bytes");

        var during = Client(Current, Legacy);
        await during.EnsureTenantBucketAsync(tenantId);
        await during.PutObjectAsync(rewritten, new MemoryStream(Encoding.UTF8.GetBytes("newer bytes")), "text/plain");

        var first = await MigrateAllAsync(during, tenantId, limit: 3);
        Assert.Equal((3, 1, 2, 2), (first.Copied, first.AlreadyPresent, first.Removed, first.Retained));
        Assert.Equal(until.ToUnixTimeSeconds(), first.RetainedUntil?.ToUnixTimeSeconds());

        using var raw = _factory.CreateStorageClient();
        var current = BucketOf(Current, tenantId);
        var copy = await raw.GetObjectMetadataAsync(current, plain);
        Assert.Equal("stands-in", copy.Metadata["x-amz-meta-sa-wrapped-dek"]);
        Assert.Equal("text/plain", copy.Headers.ContentType);
        var retention = await raw.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = current, Key = retained });
        Assert.Equal(until.ToUnixTimeSeconds(), new DateTimeOffset(DateTime.SpecifyKind(retention.Retention.RetainUntilDate!.Value, DateTimeKind.Utc)).ToUnixTimeSeconds());
        var hold = await raw.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = current, Key = onHold });
        Assert.Equal(ObjectLockLegalHoldStatus.On, hold.LegalHold.Status);

        // The newer write was not overwritten by the legacy copy.
        await using (var read = await during.GetObjectAsync(rewritten))
        {
            Assert.Equal("newer bytes", await new StreamReader(read).ReadToEndAsync());
        }

        // Removed where no lock kept it, kept where one does.
        var legacy = BucketOf(Legacy, tenantId);
        Assert.False(await RawExistsAsync(legacy, plain));
        Assert.False(await RawExistsAsync(legacy, rewritten));
        Assert.True(await RawExistsAsync(legacy, retained));
        Assert.True(await RawExistsAsync(legacy, onHold));

        // Again: nothing copied twice, the locked copies still kept.
        // A batch of ONE: the cursor rests on a kept key, which SeaweedFS lists again (StartAfter is inclusive there).
        var second = await MigrateAllAsync(during, tenantId, limit: 1);
        Assert.Equal((0, 2, 0, 2), (second.Copied, second.AlreadyPresent, second.Removed, second.Retained));

        await during.SetLegalHoldAsync(onHold, held: false);
        await before.SetLegalHoldAsync(onHold, held: false);
    }

    private static async Task<TenantBucketMigrationBatch> MigrateAllAsync(S3ObjectStorageClient client, Guid tenantId, int limit)
    {
        var sum = new TenantBucketMigrationBatch(0, 0, 0, 0, null, null);
        string? after = null;
        do
        {
            var batch = await client.MigrateBatchAsync(tenantId, after, limit);
            sum = new TenantBucketMigrationBatch(
                sum.Copied + batch.Copied, sum.AlreadyPresent + batch.AlreadyPresent, sum.Removed + batch.Removed,
                sum.Retained + batch.Retained, batch.RetainedUntil ?? sum.RetainedUntil, null);
            after = batch.Next;
        }
        while (after is not null);

        return sum;
    }

    private static async Task<string> PutAsync(
        S3ObjectStorageClient client, Guid tenantId, string content, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var key = $"{ObjectKeyPrefixes.Tenant(tenantId)}2026/{Guid.NewGuid():D}/content.txt";
        await client.PutObjectAsync(key, new MemoryStream(Encoding.UTF8.GetBytes(content)), "text/plain",
            metadata ?? new Dictionary<string, string>());
        return key;
    }

    private async Task<bool> RawExistsAsync(string bucket, string key)
    {
        using var raw = _factory.CreateStorageClient();
        try
        {
            await raw.GetObjectMetadataAsync(bucket, key);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string BucketOf(string prefix, Guid tenantId) => $"{prefix}-{tenantId:D}";

    private S3ObjectStorageClient Client(string prefix, string? legacy)
    {
        var configured = _factory.Services.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
        return new S3ObjectStorageClient(
            Options.Create(new ObjectStorageOptions
            {
                ServiceUrl = configured.ServiceUrl,
                PublicServiceUrl = configured.PublicServiceUrl,
                Region = configured.Region,
                BucketName = configured.BucketName,
                BucketPrefix = prefix,
                LegacyBucketPrefix = legacy,
                AccessKey = configured.AccessKey,
                SecretKey = configured.SecretKey,
            }),
            _factory.Services.GetRequiredService<ILogger<S3ObjectStorageClient>>());
    }
}

// The API half (ADR 1000): without a legacy prefix the rel is not advertised, so `alcifra storage migrate` reports
// "nothing to migrate" instead of calling, and a composed request is refused with a code that names why.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TenantStorageMigrationRelTests
{
    private readonly E2EApiFactory _factory;

    public TenantStorageMigrationRelTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Without_a_legacy_prefix_no_tenant_offers_a_storage_migration_and_a_composed_request_is_refused()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var tenantId = await _factory.SeedTenantNamedAsync($"Bucket rel {Guid.NewGuid():N}");

        var tenant = await TestJson.Get(admin, $"/api/tenants/{tenantId}");
        var rels = tenant.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.Contains("self", rels);
        Assert.DoesNotContain("storage-migration", rels);

        using var response = await admin.PostAsync($"/api/tenants/{tenantId}/storage-migration", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("BUCKET_MIGRATION_NOT_CONFIGURED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
