namespace Alcifra.Infrastructure.Storage;

/// <summary>
/// Reads an object's user metadata with one HEAD and decides nothing about it — what
/// <see cref="EncryptedObjectPresignGuard"/> asks before a presigned read (#1843, ADR 0953).
/// </summary>
/// <remarks>
/// Separate from <c>IObjectStorageClient.GetObjectInfoAsync</c> because that read REFUSES an encrypted object on an
/// installation without the service and logs the refusal; the guard needs the facts, so that the refusal is
/// logged once, by whoever decides what happens to it.
/// </remarks>
public interface IStoredMetadataProbe
{
    /// <summary>The object's user metadata, or null when nothing is stored under the key.</summary>
    Task<IReadOnlyDictionary<string, string>?> MetadataAsync(string objectKey, CancellationToken cancellationToken = default);
}
