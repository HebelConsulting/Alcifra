using Amazon.S3.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>The tenant buckets' start-up step around a change of bucket prefix (ADR 1000).</summary>
public static class TenantBucketStartup
{
    /// <summary>
    /// While a migration is configured, every tenant's bucket under the current prefix is created (writes land there
    /// from the first request). Without one, a prefix that moved away from where the objects are refuses to start.
    /// </summary>
    /// <remarks>
    /// Runs in both app instances, inside the start-up seed lock; creating a bucket is idempotent either way. The check
    /// for stranded objects costs one bucket probe per tenant, and a second only where the first finds nothing, which
    /// for a tenant provisioned under the current prefix never happens.
    /// </remarks>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
        var storage = services.GetRequiredService<S3ObjectStorageClient>();
        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TenantBucketStartup).FullName!);

        var tenants = await dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"])
            .Select(t => new { t.Id, t.IncompleteUploadCleanupDays })
            .ToListAsync(cancellationToken);

        if (storage.IsConfigured)
        {
            foreach (var tenant in tenants)
            {
                await storage.EnsureTenantBucketAsync(tenant.Id, cancellationToken);
                await storage.SetBucketLifecycleAsync(tenant.Id, tenant.IncompleteUploadCleanupDays, cancellationToken);
            }

            logger.LogWarning(
                "A bucket migration is configured: reads fall back from '{BucketPrefix}-<tenant>' to '{LegacyBucketPrefix}-<tenant>' "
                + "for {Tenants} tenant(s). Run 'alcifra storage migrate' and remove ObjectStorage:LegacyBucketPrefix when it "
                + "reports nothing left.",
                options.BucketPrefix, options.LegacyBucketPrefix, tenants.Count);
            return;
        }

        // The prefix the objects were under before BucketPrefix was set: BucketName is what it fell back to.
        var current = string.IsNullOrWhiteSpace(options.BucketPrefix) ? options.BucketName : options.BucketPrefix;
        if (current == options.BucketName)
        {
            return;
        }

        var stranded = 0;
        foreach (var tenant in tenants)
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(storage.InternalClient, storage.BucketForTenant(tenant.Id))
                && await AmazonS3Util.DoesS3BucketExistV2Async(storage.InternalClient, $"{options.BucketName}-{tenant.Id:D}"))
            {
                stranded++;
            }
        }

        if (stranded > 0)
        {
            throw new TenantBucketsStrandedException(current, options.BucketName, stranded);
        }
    }
}
