using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.PasswordStores;

/// <summary>What a document is to the password store (ADR 0964): its kind, and the store it belongs to.</summary>
public enum PasswordItemKind
{
    Store,
    Folder,
    Entry,
}

/// <summary>A document of a password store, read once: enough for every rel and every rights question about it.</summary>
public sealed record PasswordItem(
    Guid Id, string Name, PasswordItemKind Kind, Guid? ParentId, Guid StoreId, Guid? PersonalRootOwnerId, Guid ConcurrencyToken, Guid? CurrentVersionId);

/// <summary>
/// The password store's view of the archive's tree (ADR 0964): which documents are stores, folders and entries, and an
/// item's STORE, its nearest Password Store ancestor, which decides who its entries are sealed to.
/// </summary>
public sealed class PasswordStoreTree(SimplArchiveDbContext dbContext)
{
    /// <summary>The kind a mask makes a document, or null for a mask that is no part of a store.</summary>
    public static PasswordItemKind? KindOf(Guid? maskId) =>
        maskId == WellKnownMaskIds.PasswordStore ? PasswordItemKind.Store
        : maskId == WellKnownMaskIds.PasswordFolder ? PasswordItemKind.Folder
        : maskId == WellKnownMaskIds.PasswordEntry ? PasswordItemKind.Entry
        : null;

    /// <summary>The item, or null when the document does not exist, is not visible here or is no part of a store.</summary>
    public async Task<PasswordItem?> FindAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var row = await RowAsync(documentId, cancellationToken);
        if (row is null || KindOf(row.MaskId) is not { } kind)
        {
            return null;
        }

        // Up to the nearest store: a short walk, because containment allows folders and entries only beneath one.
        var store = row;
        while (KindOf(store.MaskId) != PasswordItemKind.Store)
        {
            if (store.ParentId is not { } up || await RowAsync(up, cancellationToken) is not { } parent)
            {
                return null;
            }

            store = parent;
        }

        return new PasswordItem(row.Id, row.Name, kind, row.ParentId, store.Id, row.PersonalRootOwnerId, row.ConcurrencyToken, row.CurrentVersionId);
    }

    /// <summary>The direct children of a store or folder that are part of it, folders first, then by name.</summary>
    public async Task<IReadOnlyList<PasswordItem>> ChildrenAsync(PasswordItem container, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Documents
            .Where(d => d.ParentId == container.Id)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.ConcurrencyToken,
                d.CurrentVersionId,
                d.PersonalRootOwnerId,
                MaskId = dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows
            .Where(r => KindOf(r.MaskId) is PasswordItemKind.Folder or PasswordItemKind.Entry)
            .Select(r => new PasswordItem(r.Id, r.Name, KindOf(r.MaskId)!.Value, container.Id, container.StoreId, r.PersonalRootOwnerId, r.ConcurrencyToken, r.CurrentVersionId))
            .OrderBy(i => i.Kind == PasswordItemKind.Folder ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Every entry beneath a store, at any depth, for the re-seal comparison.</summary>
    public async Task<IReadOnlyList<PasswordItem>> EntriesAsync(PasswordItem store, CancellationToken cancellationToken)
    {
        var entries = new List<PasswordItem>();
        var level = new List<PasswordItem> { store };
        while (level.Count > 0)
        {
            var next = new List<PasswordItem>();
            foreach (var container in level)
            {
                foreach (var child in await ChildrenAsync(container, cancellationToken))
                {
                    (child.Kind == PasswordItemKind.Entry ? entries : next).Add(child);
                }
            }

            level = next;
        }

        return entries;
    }

    /// <summary>
    /// Every folder and entry beneath a store or folder, at any depth, as ids — what the flat <c>descendants</c>
    /// listing pages over (ADR 0207; apass completion, 2026-10-25).
    /// </summary>
    /// <remarks>
    /// The walk collects the whole id set before the page is read, which is bounded by the store's size rather
    /// than the archive's: a password store is a personal tree of folders and entries, not a repository. The
    /// alternative — a recursive CTE — is the raw SQL the provider-agnostic rule forbids (ADR 0542).
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> DescendantIdsAsync(PasswordItem root, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var level = new List<PasswordItem> { root };
        while (level.Count > 0)
        {
            var next = new List<PasswordItem>();
            foreach (var container in level)
            {
                foreach (var child in await ChildrenAsync(container, cancellationToken))
                {
                    ids.Add(child.Id);
                    if (child.Kind == PasswordItemKind.Folder)
                    {
                        next.Add(child);
                    }
                }
            }

            level = next;
        }

        return ids;
    }

    /// <summary>The stores the caller's tenant holds, as ids: a client asks rights of these.</summary>
    public async Task<IReadOnlyList<Guid>> StoreIdsAsync(CancellationToken cancellationToken) =>
        await dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.PasswordStore))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

    private sealed record Row(Guid Id, string Name, Guid? ParentId, Guid? PersonalRootOwnerId, Guid ConcurrencyToken, Guid? CurrentVersionId, Guid? MaskId);

    private Task<Row?> RowAsync(Guid documentId, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new Row(d.Id, d.Name, d.ParentId, d.PersonalRootOwnerId, d.ConcurrencyToken, d.CurrentVersionId,
                dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);
}
