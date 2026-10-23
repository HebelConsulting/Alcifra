using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Concurrency;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Encryption;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.PasswordStores;

/// <summary>A version of a sealed core document about to be confirmed, with what its finalize must honour.</summary>
/// <param name="Document">The document, tracked: the finalize commits through its verb contract when it is gated.</param>
/// <param name="FirstVersion">True for an entry's first content, which has no earlier state anybody saw.</param>
/// <param name="Reseal">True when the version is a re-seal after an access change, a system act.</param>
public sealed record SealedVersionFinalize(Document Document, bool FirstVersion, bool Reseal)
{
    /// <summary>Whether the finalize carries the caller's precondition: every change of an entry that already had content.</summary>
    public bool Gated => !FirstVersion;
}

/// <summary>
/// The filing half of a core mask's sealed content (ADR 0964, mirroring ADR 0940 for modules): before a version of a
/// Password Entry is confirmed, its stored bytes must be shaped as a CMS envelope, a change of an existing entry must
/// carry the precondition of what its author saw, and a re-seal is marked as the system act it is.
/// </summary>
/// <remarks>
/// A refusal leaves nothing behind: the uploaded object and the pending version are removed, as the quota refusal does.
/// The serving half is the storage door's, which checks the stored bytes again on the strict tier (ADR 0940).
/// </remarks>
public sealed class PasswordEntryVersions(
    SimplArchiveDbContext dbContext, IObjectStorageClient storage, IServiceProvider services, ILogger<PasswordEntryVersions> logger)
{
    /// <summary>
    /// What the finalize of <paramref name="version"/> must honour, or null when its document's mask is not a sealed core
    /// mask (or the version is already confirmed, which makes the finalize a no-op).
    /// </summary>
    /// <exception cref="ContentNotSealedException">The bytes are not shaped as a CMS envelope.</exception>
    /// <exception cref="InvalidVersionCommentKindException">A system comment kind this version may not carry.</exception>
    public async Task<SealedVersionFinalize?> GuardAsync(
        DocumentVersion version, int? commentKind, HttpRequest request, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.SingleAsync(d => d.Id == version.DocumentId, cancellationToken);
        var maskId = await dbContext.MaskVersions.Where(v => v.Id == document.MaskVersionId)
            .Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
        if (maskId is not { } sealedMask || !WellKnownMaskIds.SealedContentMasks.ContainsKey(sealedMask))
        {
            return commentKind is { } other && other != (int)VersionCommentKind.UserComment
                ? throw new InvalidVersionCommentKindException(other)
                : null;
        }

        if (version.Status != DocumentVersionStatus.Pending)
        {
            return null;
        }

        var firstVersion = !await dbContext.DocumentVersions.AnyAsync(
            v => v.DocumentId == document.Id && v.Status == DocumentVersionStatus.Confirmed, cancellationToken);
        var reseal = commentKind switch
        {
            null or (int)VersionCommentKind.UserComment => false,
            (int)VersionCommentKind.ResealedAfterAccessChange when !firstVersion => true,
            var refused => throw new InvalidVersionCommentKindException(refused.Value),
        };

        if (!await IsEnvelopeAsync(version.ObjectKey, cancellationToken))
        {
            logger.LogWarning(
                "Refused version {VersionId} of password entry {DocumentId}: its content is not shaped as a CMS envelope, so it was "
                + "removed rather than filed as a sealed secret", version.Id, document.Id);
            await storage.DeleteObjectAsync(version.ObjectKey, cancellationToken);
            dbContext.DocumentVersions.Remove(version);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new ContentNotSealedException();
        }

        if (!firstVersion)
        {
            ConcurrencyHeaders.RequireIfMatch(request);
        }

        if (reseal)
        {
            version.CommentKind = VersionCommentKind.ResealedAfterAccessChange;
            version.Comment = null;
        }

        return new SealedVersionFinalize(document, firstVersion, reseal);
    }

    /// <summary>Removes the entry's "Exposed since" (ADR 0964), in the caller's unit of work: a new password was filed.</summary>
    public async Task ClearExposureAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var values = await (
            from v in dbContext.FieldValues
            join f in dbContext.FieldDefinitions on v.FieldDefinitionId equals f.Id
            where v.DocumentId == entryId && f.Name == WellKnownMaskIds.PasswordEntryFields.ExposedSince
            select v).ToListAsync(cancellationToken);
        if (values.Count > 0)
        {
            dbContext.FieldValues.RemoveRange(values);
            logger.LogInformation("Password entry {DocumentId}: a new password was filed, so it is no longer marked exposed", entryId);
        }
    }

    /// <summary>Applies the history rules to one entry now, after its new version committed (ADR 0964).</summary>
    /// <remarks>Resolved per call: the history needs the reader set, whose services this class must not construct.</remarks>
    public Task ReconcileHistoryAsync(Guid entryId, CancellationToken cancellationToken) =>
        services.GetRequiredService<PasswordEntryHistory>().ReconcileAsync(entryId, DateTimeOffset.UtcNow, null, cancellationToken);

    private async Task<bool> IsEnvelopeAsync(string objectKey, CancellationToken cancellationToken)
    {
        var size = await storage.GetObjectSizeAsync(objectKey, cancellationToken);
        if (size <= 0)
        {
            return false;
        }

        var head = new byte[CmsEnvelopeShape.HeadLength];
        int read;
        await using (var range = await storage.GetObjectRangeAsync(objectKey, 0, Math.Min(size, head.Length) - 1, cancellationToken))
        {
            read = await range.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        }

        return CmsEnvelopeShape.IsEnvelope(head.AsSpan(0, read), size);
    }
}
