using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.PasswordStores;

/// <summary>
/// The password store's two layers that need no key (ADR 0964, owner decisions 2026-10-23): almost no history, and the
/// compromise workflow on a revoked certificate. The server never decrypts; it reads each envelope's recipients, which
/// are in the clear, then MARKS and DELETES.
/// </summary>
/// <remarks>
/// <para>
/// <b>Almost no history.</b> A superseded version of an entry lives for <see cref="UndoWindow"/>, counted from when it
/// stopped being current (the current version's filing, or its own when a restore made an older one current), then its
/// sealed object is deleted for good and its row with it.
/// </para>
/// <para>
/// <b>Compromise.</b> Every entry whose current version is sealed to a revoked certificate gets
/// <see cref="WellKnownMaskIds.PasswordEntryFields.ExposedSince"/> (the revocation's date, kept when already set) and is
/// stamped for "changes since", so a phone refreshes and drops the store's offline copy. Once such an entry's current
/// version is sealed to no revoked certificate (a member re-sealed it), its superseded versions are deleted AT ONCE.
/// </para>
/// <para>
/// <b>Leaving the audience</b> (ADR 0983). Once an entry's current version is sealed only to its store's effective
/// audience (a member re-sealed it after somebody was removed), every superseded version sealed to a certificate OUTSIDE
/// that audience is deleted AT ONCE, as for a revoked certificate: the person removed must not keep a day's worth of
/// secrets their own key still opens.
/// </para>
/// <para>
/// <b>Abandoned creates.</b> An entry still without confirmed content a day after it was created (its first upload never
/// finished) is removed, by name in the audit trail.
/// </para>
/// <para>
/// <b>Every act CLAIMS before acting</b> (ADR 0836): both application instances run the sweep. Marking claims the
/// document by compare-and-swapping its token (moved, because index data an editor reads changes); deleting a version
/// claims it by moving it from Confirmed back to Pending inside its own transaction, so only one instance deletes it and
/// a failure rolls the claim back. One transaction per item.
/// </para>
/// </remarks>
public sealed class PasswordEntryHistory(
    SimplArchiveDbContext dbContext,
    IObjectStorageClient storage,
    IStorageQuotaService quota,
    PasswordStoreSealing sealing,
    SimplArchive.Api.Audience.ContentAudience audience,
    IAuditRecorder audit,
    IDocumentIndexQueue indexQueue,
    ILogger<PasswordEntryHistory> logger)
{
    /// <summary>How long a superseded version stays, as an undo window.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    /// <summary>What one pass did.</summary>
    public sealed record Outcome(int MarkedExposed, int VersionsDeleted, int AbandonedRemoved = 0)
    {
        public static Outcome operator +(Outcome a, Outcome b) =>
            new(a.MarkedExposed + b.MarkedExposed, a.VersionsDeleted + b.VersionsDeleted, a.AbandonedRemoved + b.AbandonedRemoved);
    }

    /// <summary>One pass over the ambient tenant's entries.</summary>
    public async Task<Outcome> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revoked = await sealing.RevokedAsync(cancellationToken);
        var entries = await dbContext.Documents.AsNoTracking()
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.PasswordEntry))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var outcome = new Outcome(0, 0);
        foreach (var entryId in entries)
        {
            try
            {
                outcome += await ReconcileAsync(entryId, now, revoked, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One entry's failure must not stop the others; its claim rolled back and the next pass retries it.
                logger.LogWarning(e, "The password history sweep could not reconcile entry {DocumentId}; the next pass retries it", entryId);
                dbContext.ChangeTracker.Clear();
            }
        }

        return outcome;
    }

    /// <summary>One entry: mark it exposed where a revoked certificate can read it, and delete what is due.</summary>
    /// <param name="revoked">The tenant's revoked certificates; read here when null.</param>
    public async Task<Outcome> ReconcileAsync(Guid entryId, DateTimeOffset now, IReadOnlyList<RevokedCertificate>? revoked, CancellationToken cancellationToken)
    {
        revoked ??= await sealing.RevokedAsync(cancellationToken);
        var document = await dbContext.Documents.AsNoTracking().Where(d => d.Id == entryId)
            .Select(d => new { d.Id, d.Name, d.TenantId, d.CurrentVersionId, d.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return new Outcome(0, 0);
        }

        if (await CurrentVersion.ResolveAsync(dbContext.DocumentVersions.AsNoTracking(), entryId, document.CurrentVersionId, cancellationToken) is not { } current)
        {
            // An entry whose first upload never finished: nothing sealed was ever filed, so after the undo window it goes.
            return now >= document.CreatedAt + UndoWindow
                && await RemoveAbandonedAsync(entryId, document.Name, document.TenantId, cancellationToken)
                    ? new Outcome(0, 0, 1)
                    : new Outcome(0, 0);
        }

        var recipients = await sealing.RecipientsAsync(current, cancellationToken);
        var exposedAt = revoked.Where(r => recipients.Any(x => x.Is(r.Certificate))).Select(r => (DateTimeOffset?)r.RevokedAt).Min();
        var marked = exposedAt is { } at && await MarkExposedAsync(entryId, document.Name, document.TenantId, at, cancellationToken);
        var exposed = await ExposedSinceAsync(dbContext, entryId, cancellationToken) is not null;

        // Re-sealed without the revoked key: what came before is deleted now, not after the undo window.
        var immediately = exposed && exposedAt is null;
        var superseded = await dbContext.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == entryId && v.Status == DocumentVersionStatus.Confirmed && v.Id != current.Id)
            .ToListAsync(cancellationToken);

        // Re-sealed to the audience after somebody left it: a superseded version they could still open goes now too.
        var audienceCertificates = await AudienceCertificatesAsync(entryId, cancellationToken);
        var currentWithinAudience = audienceCertificates.Count > 0
            && recipients.All(r => audienceCertificates.Any(r.Is));

        var deleted = 0;
        foreach (var version in superseded)
        {
            var leftAudience = currentWithinAudience
                && (await sealing.RecipientsAsync(version, cancellationToken)).Any(r => !audienceCertificates.Any(r.Is));
            var atOnce = immediately || leftAudience;
            var dueAt = (version.CreatedAt > current.CreatedAt ? version.CreatedAt : current.CreatedAt) + UndoWindow;
            if ((atOnce || now >= dueAt)
                && await DeleteVersionAsync(version.Id, entryId, version.ObjectKey, version.SizeBytes, document.TenantId, atOnce, cancellationToken))
            {
                deleted++;
            }
        }

        return new Outcome(marked ? 1 : 0, deleted);
    }

    /// <summary>The certificates of the entry's store's effective audience, all members together.</summary>
    private async Task<IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>> AudienceCertificatesAsync(
        Guid entryId, CancellationToken cancellationToken) =>
        await audience.RootOfAsync(entryId, cancellationToken) is { } root
            ? [.. (await audience.EffectiveAsync(root, cancellationToken))
                .SelectMany(m => m.CertificatePems)
                .Distinct(StringComparer.Ordinal)
                .Select(pem => System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem))]
            : [];

    /// <summary>The entry's exposure date, or null when it is not marked.</summary>
    public static async Task<DateOnly?> ExposedSinceAsync(SimplArchiveDbContext dbContext, Guid entryId, CancellationToken cancellationToken)
    {
        var value = await (
            from v in dbContext.FieldValues
            join f in dbContext.FieldDefinitions on v.FieldDefinitionId equals f.Id
            where v.DocumentId == entryId && f.Name == WellKnownMaskIds.PasswordEntryFields.ExposedSince
            select v.Value).FirstOrDefaultAsync(cancellationToken);
        return value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? DateOnly.FromDateTime(at.UtcDateTime)
            : null;
    }

    private async Task<bool> MarkExposedAsync(Guid entryId, string name, Guid tenantId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (await ExposedSinceAsync(dbContext, entryId, cancellationToken) is not null)
        {
            return false;
        }

        var seen = await dbContext.Documents.AsNoTracking().Where(d => d.Id == entryId)
            .Select(d => new { d.ConcurrencyToken, d.MaskVersionId, d.ParentId }).SingleAsync(cancellationToken);
        var field = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == seen.MaskVersionId && f.Name == WellKnownMaskIds.PasswordEntryFields.ExposedSince)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (field is not { } fieldId)
        {
            logger.LogWarning("Password entry {DocumentId} is readable by a revoked certificate but its mask version has no 'Exposed since' "
                + "field; it cannot be marked until the mask is healed", entryId);
            return false;
        }

        await using var transaction = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

        // THE CLAIM, and it moves the token: index data an editor reads is about to change, so a form opened before must
        // answer 412 rather than overwrite the verdict.
        var claimed = await dbContext.Documents
            .Where(d => d.Id == entryId && d.ConcurrencyToken == seen.ConcurrencyToken)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ConcurrencyToken, Guid.NewGuid()), cancellationToken);
        if (claimed == 0)
        {
            return false;   // the other instance, or an editor, got there first: the next pass looks again
        }

        dbContext.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = entryId,
            FieldDefinitionId = fieldId,
            Value = DateOnly.FromDateTime(at.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });

        // The store and the folders above are stamped too, so "changes since" names the store a phone must drop.
        await StampAncestorsAsync(seen.ParentId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        dbContext.ChangeTracker.Clear();
        logger.LogWarning("Password entry {DocumentId} is sealed to a certificate revoked on {RevokedAt:yyyy-MM-dd}: marked exposed; "
            + "its store needs re-sealing, and the secret should be changed", entryId, at);
        await audit.RecordForActorAsync(AuditActorType.System, Guid.Empty, "Password store", tenantId,
            AuditActions.PasswordEntryExposed, "Document", entryId, name, $"Exposed since {at:yyyy-MM-dd}", cancellationToken);
        return true;
    }

    private async Task StampAncestorsAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        while (parentId is { } id && await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == id, cancellationToken) is { } folder)
        {
            var maskId = await dbContext.MaskVersions.Where(v => v.Id == folder.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
            if (PasswordStoreTree.KindOf(maskId) is not (PasswordItemKind.Store or PasswordItemKind.Folder))
            {
                return;
            }

            folder.ChangedAt = now;   // a stamp alone keeps the folder's ETag (ADR 0925)
            parentId = PasswordStoreTree.KindOf(maskId) == PasswordItemKind.Store ? null : folder.ParentId;
        }
    }

    /// <summary>
    /// Removes an entry that never received content (owner decision 2026-10-23), CLAIMED by the one statement that deletes
    /// it only while it still has no confirmed version: of two instances, one deletes the row and the other affects none.
    /// Its pending uploads' objects go with it; the row's dependents cascade.
    /// </summary>
    internal async Task<bool> RemoveAbandonedAsync(Guid entryId, string name, Guid tenantId, CancellationToken cancellationToken)
    {
        var pendingKeys = await dbContext.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == entryId).Select(v => v.ObjectKey).ToListAsync(cancellationToken);

        await using var transaction = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var removed = await dbContext.Documents
            .Where(d => d.Id == entryId
                && !dbContext.DocumentVersions.Any(v => v.DocumentId == entryId && v.Status == DocumentVersionStatus.Confirmed))
            .ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            return false;   // the other instance removed it, or content arrived meanwhile
        }

        foreach (var key in pendingKeys)
        {
            await storage.DeleteObjectForGoodAsync(key, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await indexQueue.EnqueueAsync(entryId, cancellationToken);
        logger.LogInformation("Removed password entry {DocumentId}: it received no content within {Window}", entryId, UndoWindow);
        await audit.RecordForActorAsync(AuditActorType.System, Guid.Empty, "Password store", tenantId,
            AuditActions.PasswordEntryAbandonedRemoved, "Document", entryId, name, "No content within 24 hours", cancellationToken);
        return true;
    }

    internal async Task<bool> DeleteVersionAsync(
        Guid versionId, Guid entryId, string objectKey, long? sizeBytes, Guid tenantId, bool compromise, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

        // THE CLAIM: Confirmed back to Pending, compare-and-swap. Only one instance moves it; a failure below rolls it back.
        // A pending version carries no number and no hash (the table's check), so the claim clears both.
        var claimed = await dbContext.DocumentVersions
            .Where(v => v.Id == versionId && v.Status == DocumentVersionStatus.Confirmed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, DocumentVersionStatus.Pending)
                .SetProperty(v => v.VersionNumber, (int?)null)
                .SetProperty(v => v.Sha256Hash, (string?)null), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        // What anchors to the version goes with it (their foreign keys restrict, ADR 0964): the version-added chat line,
        // and any annotation.
        await dbContext.ChatMessages.Where(c => c.DocumentVersionId == versionId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.DocumentAnnotations.Where(a => a.DocumentVersionId == versionId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.DocumentVersions.Where(v => v.Id == versionId).ExecuteDeleteAsync(cancellationToken);
        await storage.DeleteObjectForGoodAsync(objectKey, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (sizeBytes is { } size)
        {
            await quota.AdjustUsageAsync(tenantId, -size, cancellationToken);
        }

        logger.LogDebug("Deleted superseded version {VersionId} of password entry {DocumentId} for good ({Why})",
            versionId, entryId, compromise ? "re-sealed after a revocation or an audience change" : "its undo window ended");
        return true;
    }
}
