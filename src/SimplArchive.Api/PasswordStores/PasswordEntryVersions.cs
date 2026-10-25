using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Concurrency;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
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
    SimplArchiveDbContext dbContext, IObjectStorageClient storage, IServiceProvider services,
    ICurrentUserAccessor currentUser, ILogger<PasswordEntryVersions> logger)
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
            await DiscardAsync(version, cancellationToken);
            throw new ContentNotSealedException();
        }

        if (reseal && sealedMask == WellKnownMaskIds.SealedDocument)
        {
            // "CHANGE AUDIENCE…" IS THE AUTHOR'S ACT (ADR 0975), and the server is where that is decided. A re-seal
            // hands the content to a NEW audience, so it must be the act of whoever filed the version being
            // re-sealed — otherwise a reader who can open the content could pass it on. A Password Entry's re-seal is
            // a different act (a reader re-addressing the store after a revocation, ADR 0964) and stays open to its
            // readers, which is why this check names the Sealed DOCUMENT.
            var currentAuthor = await dbContext.DocumentVersions
                .Where(v => v.DocumentId == document.Id && v.Status == DocumentVersionStatus.Confirmed)
                .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
                .Select(v => v.CreatedByUserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (currentUser.UserId is not { } caller || currentAuthor != caller)
            {
                // Leaves nothing behind, as the shape refusal does: a caller who may not change the audience must not be
                // able to fill the document's history — or the bucket — by asking.
                await DiscardAsync(version, cancellationToken);
                throw new AudienceChangeRequiresTheAuthorException();
            }
        }

        if (!firstVersion)
        {
            // A MISSING precondition is a refusal like the others, and leaves nothing behind like the others: without
            // the cleanup, a caller that simply forgot If-Match would fill the document's history and the bucket one
            // pending version at a time — the shape refusal's own rule, applied to the one refusal that is thrown by a
            // helper rather than here.
            try
            {
                ConcurrencyHeaders.RequireIfMatch(request);
            }
            catch (IfMatchRequiredException)
            {
                await DiscardAsync(version, cancellationToken);
                throw;
            }
        }

        if (reseal)
        {
            version.CommentKind = VersionCommentKind.ResealedAfterAccessChange;
            version.Comment = null;
        }

        return new SealedVersionFinalize(document, firstVersion, reseal);
    }

    /// <summary>
    /// Removes a refused pending version and its uploaded object, committed before the refusal surfaces — a caller
    /// whose version was refused must not be able to fill the document's history or the bucket by asking again.
    /// </summary>
    private async Task DiscardAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        await storage.DeleteObjectAsync(version.ObjectKey, cancellationToken);
        dbContext.DocumentVersions.Remove(version);
        await dbContext.SaveChangesAsync(cancellationToken);
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
