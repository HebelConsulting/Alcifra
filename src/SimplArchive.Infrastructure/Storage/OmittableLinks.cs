using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Storage;

/// <summary>
/// The one place a resource that EMBEDS a presigned link turns "this installation cannot serve that object" into an
/// absent link (#1843, ADR 0953).
/// </summary>
/// <remarks>
/// <para>
/// <b>Drop the link, keep the resource.</b> Without an encryption service, <see cref="EncryptedObjectPresignGuard"/>
/// refuses to presign a read of an object encrypted at rest. A resource that only CARRIES such a link (a version's
/// download and preview, a check-out row, an intray item) must still answer, so its details, index data and chat
/// stay reachable: the link is simply absent, exactly as the strict tier's null makes it absent (ADR 0543, a
/// missing rel means "not available to you, here, now"). FOLLOWING a link (an explicit presign or redirect
/// endpoint) does not come through here, and keeps answering 503 <c>ENCRYPTION_SERVICE_NOT_CONFIGURED</c>.
/// </para>
/// <para>
/// <b>The one log line of the refusal is written here, at Warning</b>: the caller sees an absent rel and nothing
/// that says why, so this is a silent degradation in ADR 0626's sense. It names the rel, the object key and the
/// metadata that marks it (never the wrapped key's value), and says that Trace carries the storage exchange.
/// </para>
/// </remarks>
public sealed class OmittableLinks(ILogger<OmittableLinks> logger)
{
    /// <summary>
    /// The presign's result, or null when the installation refused it for want of an encryption service.
    /// </summary>
    /// <param name="presign">The presign, already started: the same call the caller would await directly.</param>
    /// <param name="rel">The rel the link would have carried, for the log line.</param>
    public async Task<T?> OrOmittedAsync<T>(Task<T?> presign, string rel) where T : class
    {
        try
        {
            return await presign;
        }
        catch (EncryptedObjectWithoutEncryptionServiceException refused)
        {
            logger.LogWarning(
                "Omitted the {Rel} link to {ObjectKey}: it is encrypted at rest ({Metadata} is present) and this "
                + "installation has no Encryption:ServiceUrl, so the link would hand out ciphertext. The resource is "
                + "served without it. Restore the encryption service configuration this store was written under. The "
                + "storage exchange is at Trace.",
                rel, refused.ObjectKey, EncryptingObjectStorageClient.WrappedDekKey);
            return null;
        }
    }
}
