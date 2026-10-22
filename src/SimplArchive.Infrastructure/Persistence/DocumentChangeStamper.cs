using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Stamps <see cref="Document.ChangedAt"/> at the ONE place every write path passes through (ADR 0925): a document
/// added or modified, the document a version, an index value or a chat message belongs to, and the folder a reference
/// is placed into or removed from (ADR 0959). "Changes since"
/// pages by the stamp, so an offline copy learns what changed without re-reading what did not.
/// </summary>
/// <remarks>
/// <para>
/// A document reached only through a related row is loaded (one query for all of them) and stamped. Such a save
/// changes nothing an editor of the document is editing, so <see cref="IsStampOnly"/> lets the context keep its
/// ETag: a chat message must not make somebody's open details form answer 412.
/// </para>
/// <para>
/// Not stamped: tags, annotations, reminders, subscriptions and permissions. A copy keeps none of the first four,
/// and a lost right is found by the phone's daily re-check, not by stamping every document beneath an ACL.
/// </para>
/// </remarks>
internal static class DocumentChangeStamper
{
    internal static async Task StampAsync(SimplArchiveDbContext context, CancellationToken cancellationToken)
    {
        var tracker = context.ChangeTracker;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in tracker.Entries<Document>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.ChangedAt = now;
        }

        var related = tracker.Entries<DocumentVersion>().Where(Changed).Select(e => e.Entity.DocumentId)
            .Concat(tracker.Entries<FieldValue>().Where(Changed).Select(e => e.Entity.DocumentId))
            .Concat(tracker.Entries<ChatMessage>().Where(Changed).Select(e => e.Entity.DocumentId))
            // A reference placed or removed changes its FOLDER (ADR 0959; owner decision 2026-10-22): the phone's next
            // refresh exports that folder again, and the target arrives in, or leaves, the offline copy.
            .Concat(tracker.Entries<DocumentReference>().Where(Changed).Select(e => e.Entity.ParentFolderId))
            .ToHashSet();
        related.ExceptWith(tracker.Entries<Document>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Entity.Id));
        if (related.Count == 0)
        {
            return;
        }

        var tracked = tracker.Entries<Document>().Where(e => related.Contains(e.Entity.Id)).Select(e => e.Entity).ToList();
        var missing = related.Except(tracked.Select(d => d.Id)).ToList();
        if (missing.Count > 0)
        {
            // A deleted document's chat or versions still describe it; soft-deleted rows are stamped too.
            tracked.AddRange(await context.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
                .Where(d => missing.Contains(d.Id)).ToListAsync(cancellationToken));
        }

        foreach (var document in tracked)
        {
            document.ChangedAt = now;
        }
    }

    /// <summary>A modified document whose only change is the stamp: its ETag stays (see the remarks).</summary>
    internal static bool IsStampOnly(EntityEntry entry) =>
        entry.State == EntityState.Modified
        && entry.Entity is Document
        && entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).SequenceEqual([nameof(Document.ChangedAt)]);

    private static bool Changed(EntityEntry entry) => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;
}
