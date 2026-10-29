using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The storage seam of an installation WITHOUT an encryption service: refuses to presign a READ of an object
/// that is encrypted at rest, and passes everything else through untouched (#1843, ADR 0953).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Without <c>Encryption:ServiceUrl</c> the encrypting decorator is never installed, so
/// nothing that presigns knows an object carries <c>sa-wrapped-dek</c>. The plain client's server-side reads
/// already refuse such an object (<see cref="EncryptedObjectWithoutEncryptionServiceException"/>, #1499), but a
/// presign is a local signature that never asks the store — so a preview or a download URL handed the
/// AES-GCM ciphertext straight to the browser, which rendered it as text (the Demo tenant's templates,
/// 2026-10-17). The server's reads said "serving it would hand out ciphertext"; the presign did exactly that.
/// </para>
/// <para>
/// <b>The price is one HEAD per presigned read</b>, paid only on installations with no encryption service,
/// accepted by the owner over the alternative of refusing to start (ADR 0953). The HEAD is the plain client's
/// <see cref="IStoredMetadataProbe"/>, which reads the same metadata its refusing reads read, so the guard and
/// the reads cannot disagree about what "encrypted" means.
/// </para>
/// <para>
/// <b>A THROW, and the caller decides what it means.</b> FOLLOWING a link (an explicit presign or redirect
/// endpoint, a module's content URL) answers the reads' own 503 <c>ENCRYPTION_SERVICE_NOT_CONFIGURED</c>. A
/// resource that merely EMBEDS a link wraps the presign in <see cref="OmittableLinks.OrOmittedAsync{T}"/> and
/// drops the link, exactly as it drops the strict tier's null, so the document's details stay reachable.
/// </para>
/// <para>
/// <b>What is not guarded, and why.</b> Uploads: a presigned PUT hands nothing out. The ciphertext URL: its
/// purpose IS ciphertext, it is signed for the encryption service's internal fetch and never reaches a client,
/// and its only caller cannot run without that service. A missing object presigns as before: there is nothing
/// to hand out, and callers already rely on presigning ahead of a write.
/// </para>
/// <para>
/// <b>Every member is forwarded explicitly</b>, including those with a default body on the interface: a default
/// reached through this class would run against THIS type, silently replacing the S3 client's real override
/// (the range read, the metadata write) with the fake-friendly fallback. <c>EncryptedObjectPresignGuardTests</c>
/// fails on any member left to its default.
/// </para>
/// </remarks>
public sealed class EncryptedObjectPresignGuard(
    IObjectStorageClient inner,
    IStoredMetadataProbe probe) : IObjectStorageClient
{
    // ---- Presigned READS: the guarded doors ----------------------------------------------------------------

    public async Task<Uri?> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan expiry, string? downloadFileName = null, string? contentType = null, CancellationToken cancellationToken = default)
    {
        await RefuseIfEncryptedAsync(objectKey, cancellationToken);
        return await inner.GetPresignedDownloadUrlAsync(objectKey, expiry, downloadFileName, contentType, cancellationToken);
    }

    public async Task<Uri?> GetPresignedPreviewUrlAsync(string objectKey, TimeSpan expiry, string? fileName = null, string? contentType = null, CancellationToken cancellationToken = default)
    {
        await RefuseIfEncryptedAsync(objectKey, cancellationToken);
        return await inner.GetPresignedPreviewUrlAsync(objectKey, expiry, fileName, contentType, cancellationToken);
    }

    /// <summary>
    /// Asks the store whether the object carries a wrapped DEK, and refuses the presign when it does.
    /// </summary>
    /// <remarks>
    /// <b>Logs nothing</b>, so a refusal is ONE line, written by whoever decides its fate: a resource that drops the
    /// link (<see cref="OmittableLinks"/>, a Warning, because the caller sees only an absent rel), or the 503 an
    /// explicit follow becomes (<c>ApiExceptionHandler</c>'s line for every 5xx, carrying the exception's message).
    /// </remarks>
    private async Task RefuseIfEncryptedAsync(string objectKey, CancellationToken cancellationToken)
    {
        // Null: nothing stored under the key, so nothing to hand out and the presign is issued exactly as before.
        if (await probe.MetadataAsync(objectKey, cancellationToken) is { } metadata
            && AtRestMetadata.IsEncrypted(metadata))
        {
            throw new EncryptedObjectWithoutEncryptionServiceException(objectKey);
        }
    }

    // ---- Presigned writes and the ciphertext address: not guarded (see the remarks) ----------------------

    public Task<Uri> GetPresignedUploadUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        inner.GetPresignedUploadUrlAsync(objectKey, expiry, cancellationToken);

    public Task<Uri> GetPresignedCiphertextUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        inner.GetPresignedCiphertextUrlAsync(objectKey, expiry, cancellationToken);

    // ---- Everything else: forwarded --------------------------------------------------------------------------

    public Task EnsureTenantBucketAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        inner.EnsureTenantBucketAsync(tenantId, cancellationToken);

    public Task SetBucketLifecycleAsync(Guid tenantId, int incompleteUploadCleanupDays, CancellationToken cancellationToken = default) =>
        inner.SetBucketLifecycleAsync(tenantId, incompleteUploadCleanupDays, cancellationToken);

    public Task<Stream> GetObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetObjectAsync(objectKey, cancellationToken);

    public Task<Stream> GetObjectForClientAsync(string objectKey, string door, CancellationToken cancellationToken = default) =>
        inner.GetObjectForClientAsync(objectKey, door, cancellationToken);

    public Task<Stream> GetObjectRangeForClientAsync(string objectKey, long from, long to, string door, CancellationToken cancellationToken = default) =>
        inner.GetObjectRangeForClientAsync(objectKey, from, to, door, cancellationToken);

    public Task<StoredObject> GetObjectWithMetadataAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetObjectWithMetadataAsync(objectKey, cancellationToken);

    public Task<StoredObjectInfo> GetObjectInfoAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetObjectInfoAsync(objectKey, cancellationToken);

    public Task<Stream> GetObjectRangeAsync(string objectKey, long from, long to, CancellationToken cancellationToken = default) =>
        inner.GetObjectRangeAsync(objectKey, from, to, cancellationToken);

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(objectKey, cancellationToken);

    public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetObjectSizeAsync(objectKey, cancellationToken);

    public Task PutObjectAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        inner.PutObjectAsync(objectKey, content, contentType, cancellationToken);

    public Task PutObjectAsync(string objectKey, Stream content, string contentType,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        inner.PutObjectAsync(objectKey, content, contentType, metadata, cancellationToken);

    public Task<bool> TryPutIfAbsentAsync(string objectKey, Stream content, string contentType,
        CancellationToken cancellationToken = default) =>
        inner.TryPutIfAbsentAsync(objectKey, content, contentType, cancellationToken);

    public Task<bool> TryPutIfMatchAsync(string objectKey, string eTag, Stream content, string contentType,
        CancellationToken cancellationToken = default) =>
        inner.TryPutIfMatchAsync(objectKey, eTag, content, contentType, cancellationToken);

    public Task<IReadOnlyList<StorageObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default) =>
        inner.ListObjectsAsync(prefix, cancellationToken);

    public Task<IReadOnlyList<StoredObjectVersion>> ListObjectVersionsAsync(string prefix, CancellationToken cancellationToken = default) =>
        inner.ListObjectVersionsAsync(prefix, cancellationToken);

    public Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) =>
        inner.CopyObjectAsync(sourceKey, destinationKey, cancellationToken);

    public Task SetObjectMetadataAsync(string objectKey, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        inner.SetObjectMetadataAsync(objectKey, metadata, cancellationToken);

    public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.DeleteObjectAsync(objectKey, cancellationToken);

    public Task DeleteObjectForGoodAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.DeleteObjectForGoodAsync(objectKey, cancellationToken);

    public Task SetRetentionAsync(string objectKey, DateTimeOffset retainUntil, WormLockMode mode, CancellationToken cancellationToken = default) =>
        inner.SetRetentionAsync(objectKey, retainUntil, mode, cancellationToken);

    public Task SetLegalHoldAsync(string objectKey, bool held, CancellationToken cancellationToken = default) =>
        inner.SetLegalHoldAsync(objectKey, held, cancellationToken);

    public Task<ObjectLockStatus> GetLockStatusAsync(string objectKey, CancellationToken cancellationToken = default) =>
        inner.GetLockStatusAsync(objectKey, cancellationToken);
}
