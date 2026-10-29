using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// Moves objects from a tenant's legacy bucket into its current one (ADR 1000): the batch the CLI drives, and the
/// single-object step the read-through client takes before it changes an object that is still only in the legacy bucket.
/// </summary>
/// <remarks>
/// <para>
/// <b>A copy is written only where the tenant's bucket lacks the key</b>, so a write made since the legacy copy is never
/// overwritten. The check and the copy are two requests, so a write landing between them loses to the copy; every key
/// the archive writes is a fresh one (ObjectKeyBuilder) except derived renditions, which are recomputed alike.
/// </para>
/// <para>
/// <b>A server-side copy carries the bytes, the content type and the user metadata</b> (the wrapped data key among it,
/// ADR 0818, which is not bound to the bucket) <b>but not the lock</b>: retention and legal hold are object state of the
/// source, so they are read there and set on the copy explicitly.
/// </para>
/// </remarks>
internal sealed class TenantBucketMigration(
    IAmazonS3 client, Func<Guid, string> bucketFor, Func<Guid, string> legacyBucketFor, ILogger logger)
{
    public async Task<TenantBucketMigrationBatch> MigrateBatchAsync(Guid tenantId, string? after, int limit, CancellationToken cancellationToken)
    {
        var legacy = legacyBucketFor(tenantId);
        var target = bucketFor(tenantId);

        ListObjectsV2Response listing;
        try
        {
            // One more than the batch, and anything not strictly after the cursor dropped: in an object-lock (versioned)
            // bucket SeaweedFS answers StartAfter INCLUSIVELY (measured 2026-10-29: after "b" → b, c, d), so a cursor
            // resting on a key a lock keeps would be listed again, and with a batch of one, forever.
            listing = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = legacy, StartAfter = after, MaxKeys = limit + 1 }, cancellationToken);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // No legacy bucket: a tenant provisioned after the prefix changed, or one already emptied and removed.
            logger.LogDebug("Tenant {TenantId} has no legacy bucket {Bucket}: nothing to migrate.", tenantId, legacy);
            return new TenantBucketMigrationBatch(0, 0, 0, 0, null, null);
        }

        var items = (listing.S3Objects ?? [])
            .Where(o => after is null || string.CompareOrdinal(o.Key, after) > 0)
            .ToList();
        var more = items.Count > limit || listing.IsTruncated == true;

        int copied = 0, present = 0, removed = 0, retained = 0;
        DateTimeOffset? retainedUntil = null;
        string? last = null;
        foreach (var item in items.Take(limit))
        {
            last = item.Key;
            var lockStatus = await LockOfAsync(client, legacy, item.Key, cancellationToken);
            if (await CopyAcrossAsync(client, legacy, target, item.Key, lockStatus, cancellationToken))
            {
                copied++;
            }
            else
            {
                present++;
            }

            if (lockStatus.IsLocked(DateTimeOffset.UtcNow) || !await TryRemoveAsync(legacy, item.Key, cancellationToken))
            {
                retained++;
                if (lockStatus.RetainUntil is { } until && (retainedUntil is null || until > retainedUntil))
                {
                    retainedUntil = until;
                }

                continue;
            }

            removed++;
        }

        logger.LogInformation(
            "Migrated a batch of tenant {TenantId} from {LegacyBucket} to {Bucket}: {Copied} copied, {Present} already there, "
            + "{Removed} legacy copies removed, {Retained} kept by a lock.",
            tenantId, legacy, target, copied, present, removed, retained);

        // A cursor that did not move ends the walk rather than repeating it.
        return new TenantBucketMigrationBatch(copied, present, removed, retained, retainedUntil,
            more && last is not null && last != after ? last : null);
    }

    /// <summary>Copies one object into the target bucket unless it already holds the key; true when it copied.</summary>
    public static async Task<bool> CopyAcrossAsync(IAmazonS3 client, string legacy, string target, string key, CancellationToken cancellationToken)
        => await CopyAcrossAsync(client, legacy, target, key, await LockOfAsync(client, legacy, key, cancellationToken), cancellationToken);

    private static async Task<bool> CopyAcrossAsync(
        IAmazonS3 client, string legacy, string target, string key, LegacyLock lockStatus, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(client, target, key, cancellationToken))
        {
            return false;
        }

        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = legacy,
            SourceKey = key,
            DestinationBucket = target,
            DestinationKey = key,
            MetadataDirective = S3MetadataDirective.COPY,
        }, cancellationToken);

        if (lockStatus is { RetainUntil: { } until, Mode: { } mode } && until > DateTimeOffset.UtcNow)
        {
            await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = target,
                Key = key,
                Retention = new ObjectLockRetention { Mode = mode, RetainUntilDate = until.UtcDateTime },
            }, cancellationToken);
        }

        if (lockStatus.LegalHold)
        {
            await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = target,
                Key = key,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
            }, cancellationToken);
        }

        return true;
    }

    /// <summary>Removes every stored version of one key (an object-lock bucket is versioned); false when a lock refuses.</summary>
    private async Task<bool> TryRemoveAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteAllVersionsAsync(client, bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.Forbidden)
        {
            // An OLDER version still under retention: the current one was free, a historical one is not. Kept, and a
            // later run removes it; Trace carries the exchange (ADR 0626).
            logger.LogWarning(
                "A version of {ObjectKey} in {Bucket} is still locked ({ErrorCode}); its legacy copy stays until a later run. "
                + "Enable Trace on {TraceSource} to see the exchange.",
                key, bucket, e.ErrorCode ?? "(none)", typeof(S3ObjectStorageClient).FullName);
            return false;
        }
    }

    public static async Task DeleteAllVersionsAsync(IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? versionIdMarker = null;
        ListVersionsResponse response;
        do
        {
            response = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                Prefix = key,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
            }, cancellationToken);

            foreach (var version in (response.Versions ?? []).Where(v => v.Key == key && !string.IsNullOrEmpty(v.VersionId) && v.VersionId != "null"))
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = version.VersionId }, cancellationToken);
            }

            keyMarker = response.NextKeyMarker;
            versionIdMarker = response.NextVersionIdMarker;
        }
        while (response.IsTruncated == true);

        // An unversioned bucket lists its objects with a "null" version id: the plain delete removes them for good.
        await client.DeleteObjectAsync(bucket, key, cancellationToken);
    }

    public static async Task<bool> ExistsAsync(IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    // The object's lock, where "no lock configuration" reads as unlocked. The object is known to exist here (it was just
    // listed or copied), so the 404 ambiguity GetLockStatusAsync resolves with a HEAD does not arise.
    private sealed record LegacyLock(DateTimeOffset? RetainUntil, ObjectLockRetentionMode? Mode, bool LegalHold)
    {
        public bool IsLocked(DateTimeOffset now) => new ObjectLockStatus(RetainUntil, LegalHold).IsLocked(now);
    }

    private static async Task<LegacyLock> LockOfAsync(IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        DateTimeOffset? retainUntil = null;
        ObjectLockRetentionMode? mode = null;
        try
        {
            var retention = await client.GetObjectRetentionAsync(
                new GetObjectRetentionRequest { BucketName = bucket, Key = key }, cancellationToken);
            if (retention.Retention?.RetainUntilDate is { } until)
            {
                retainUntil = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
                mode = retention.Retention.Mode;
            }
        }
        catch (AmazonS3Exception e) when (S3ObjectStorageClient.IsNoLockConfiguration(e))
        {
        }

        var legalHold = false;
        try
        {
            var hold = await client.GetObjectLegalHoldAsync(
                new GetObjectLegalHoldRequest { BucketName = bucket, Key = key }, cancellationToken);
            legalHold = hold.LegalHold?.Status == ObjectLockLegalHoldStatus.On;
        }
        catch (AmazonS3Exception e) when (S3ObjectStorageClient.IsNoLockConfiguration(e))
        {
        }

        return new LegacyLock(retainUntil, mode, legalHold);
    }
}
