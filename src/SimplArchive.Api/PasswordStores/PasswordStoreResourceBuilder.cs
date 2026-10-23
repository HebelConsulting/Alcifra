using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Api.PasswordStores;

/// <summary>
/// Builds the password store's resources with their rels and capability flags (ADRs 0543/0719, 0964), so the three
/// controllers that answer with them advertise the same affordances for the same rights.
/// </summary>
public sealed class PasswordStoreResourceBuilder(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    PasswordStoreTree tree,
    PasswordStoreReaders readers,
    PasswordStoreSealing sealing,
    IObjectStorageClient storage,
    OmittableLinks omittable,
    ICurrentUserAccessor currentUser)
{
    private static readonly TimeSpan ContentLinkExpiry = TimeSpan.FromMinutes(15);

    public static string FolderHref(Guid id) => $"/api/password-folders/{id}";

    public static string EntryHref(Guid id) => $"/api/password-entries/{id}";

    public static string StoreHref(Guid storeId, string sub) => $"/api/password-stores/{storeId}/{sub}";

    public static string Tag(Guid token) => $"\"{token}\"";

    /// <summary>A store or a folder with its contents, the store's re-seal state and the rels its rights allow.</summary>
    public async Task<PasswordFolderResource> FolderAsync(PasswordItem folder, CancellationToken cancellationToken)
    {
        var rights = await access.GetCallerRightsAsync(folder.Id, cancellationToken);
        var children = await tree.ChildrenAsync(folder, cancellationToken);
        var childRights = await access.GetCallerRightsForManyAsync([.. children.Select(c => c.Id)], cancellationToken);
        var store = folder.Kind == PasswordItemKind.Store ? folder : await tree.FindAsync(folder.StoreId, cancellationToken) ?? folder;
        var state = await sealing.StateAsync(store, cancellationToken);
        var needsReseal = state.EntriesNeedingReseal.Count > 0;

        var resource = new PasswordFolderResource
        {
            Id = folder.Id,
            Name = folder.Name,
            Kind = folder.Kind == PasswordItemKind.Store ? "store" : "folder",
            StoreId = folder.StoreId,
            IsPersonal = IsPersonal(folder),
            ETag = Tag(folder.ConcurrencyToken),
            NeedsReseal = needsReseal,
            UrgentReseal = state.Urgent,
            ReaderCertificateCount = (await readers.CertificatesAsync(folder.StoreId, cancellationToken)).Count,
            Items =
            [
                .. children
                    .Where(c => childRights.TryGetValue(c.Id, out var r) && r.CanSee)
                    .Select(c => Row(c, childRights[c.Id])),
            ],
        };

        resource.Links.Add(new Link("self", FolderHref(folder.Id), "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{folder.Id}", "GET"));
        resource.Links.Add(new Link("password-store-recipients", StoreHref(folder.StoreId, "recipients"), "GET"));
        if (folder.Kind == PasswordItemKind.Folder)
        {
            resource.Links.Add(new Link("store", FolderHref(folder.StoreId), "GET"));
            if (folder.ParentId is { } parentId)
            {
                resource.Links.Add(new Link("parent", FolderHref(parentId), "GET"));
            }
        }

        if (rights.CanCreateSubItems)
        {
            resource.Links.Add(new Link("password-entries", $"{FolderHref(folder.Id)}/entries", "POST"));
            resource.Links.Add(new Link("password-folders", $"{FolderHref(folder.Id)}/folders", "POST"));
        }

        if (needsReseal && (await access.GetCallerRightsAsync(folder.StoreId, cancellationToken)).CanEditContent)
        {
            resource.Links.Add(new Link("reseal", StoreHref(folder.StoreId, "reseal"), "GET"));
        }

        return resource;
    }

    /// <summary>One entry, with its content link (a presigned read of the sealed bytes) while the caller may read it.</summary>
    public async Task<PasswordEntryResource> EntryAsync(PasswordItem entry, CancellationToken cancellationToken)
    {
        var rights = await access.GetCallerRightsAsync(entry.Id, cancellationToken);
        var version = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, entry.Id, entry.CurrentVersionId, cancellationToken);
        var certificates = await readers.CertificatesAsync(entry.StoreId, cancellationToken);

        var resource = new PasswordEntryResource
        {
            Id = entry.Id,
            Name = entry.Name,
            StoreId = entry.StoreId,
            ParentId = entry.ParentId ?? entry.StoreId,
            CurrentVersionId = version?.Id,
            VersionNumber = version?.VersionNumber,
            ETag = Tag(entry.ConcurrencyToken),
            NeedsReseal = await sealing.NeedsResealAsync(entry, certificates, cancellationToken),
            ExposedSince = (await PasswordEntryHistory.ExposedSinceAsync(dbContext, entry.Id, cancellationToken))?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            CanEdit = rights.CanEditContent,
            CanRename = rights.CanEditIndexData,
            CanDelete = rights.CanDelete,
            CanMove = rights.CanMove,
        };

        resource.Links.Add(new Link("self", EntryHref(entry.Id), "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{entry.Id}", "GET"));
        resource.Links.Add(new Link("parent", FolderHref(resource.ParentId), "GET"));
        resource.Links.Add(new Link("store", FolderHref(entry.StoreId), "GET"));
        resource.Links.Add(new Link("password-store-recipients", StoreHref(entry.StoreId, "recipients"), "GET"));

        // The sealed bytes as stored, typed as opaque bytes so no client's content funnel mistakes them for a
        // strict-tier S/MIME delivery: the password store client opens them itself (ADR 0964).
        if (rights.CanReadContent && version is not null
            && await omittable.OrOmittedAsync(
                storage.GetPresignedPreviewUrlAsync(version.ObjectKey, ContentLinkExpiry, $"{entry.Name}.p7m", "application/octet-stream", cancellationToken),
                "content") is { } content)
        {
            resource.Links.Add(new Link("content", content.ToString(), "GET"));
        }

        if (rights.CanEditContent)
        {
            resource.Links.Add(new Link("versions", $"/api/documents/{entry.Id}/versions", "POST"));
        }

        if (rights.CanMove)
        {
            resource.Links.Add(new Link("move", $"/api/documents/{entry.Id}/parent", "PUT"));
        }

        return resource;
    }

    private PasswordItemResource Row(PasswordItem item, EffectiveRights rights)
    {
        var row = new PasswordItemResource
        {
            Id = item.Id,
            Name = item.Name,
            Kind = item.Kind == PasswordItemKind.Folder ? "folder" : "entry",
            ETag = Tag(item.ConcurrencyToken),
            CanEdit = rights.CanEditContent,
            CanRename = rights.CanEditIndexData,
            CanDelete = rights.CanDelete,
            CanMove = rights.CanMove,
        };

        // `document` serves the rename (PUT) and the delete (DELETE) as well, under the flags (ADR 0719).
        row.Links.Add(new Link("self", item.Kind == PasswordItemKind.Folder ? FolderHref(item.Id) : EntryHref(item.Id), "GET"));
        row.Links.Add(new Link("document", $"/api/documents/{item.Id}", "GET"));
        if (rights.CanMove)
        {
            row.Links.Add(new Link("move", $"/api/documents/{item.Id}/parent", "PUT"));
        }

        return row;
    }

    private bool IsPersonal(PasswordItem item) => item.PersonalRootOwnerId is { } owner && owner == currentUser.UserId;
}
