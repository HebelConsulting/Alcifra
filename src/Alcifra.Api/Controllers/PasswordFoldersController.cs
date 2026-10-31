using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Documents;
using Alcifra.Api.Errors.Exceptions.PasswordStores;
using Alcifra.Api.Pagination;
using Alcifra.Api.PasswordStores;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// A password store or a folder in one (ADR 0964): its contents, and the creation of folders and entries inside it.
/// Rename, move and delete are the document's own doors (<c>document</c> and <c>move</c> rels on every row).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/password-folders/{folderId:guid}")]
[Authorize]
public class PasswordFoldersController(
    AlcifraDbContext dbContext,
    DocumentAccessService access,
    PasswordStoreTree tree,
    PasswordStoreResourceBuilder resources,
    PasswordEntryCreation entryCreation,
    IDocumentIndexQueue queue,
    IAuditRecorder audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await VisibleFolderAsync(folderId, cancellationToken);
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(folder.ConcurrencyToken);
        return Ok(await resources.FolderAsync(folder, cancellationToken));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await VisibleFolderAsync(folderId, cancellationToken);
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(folder.ConcurrencyToken);
        return NoContent();
    }

    /// <summary>A new Password Folder inside this store or folder.</summary>
    [HttpPost("folders")]
    public async Task<IActionResult> CreateFolder(Guid folderId, [FromBody] CreatePasswordItemRequest request, CancellationToken cancellationToken)
    {
        var child = await CreateAsync(folderId, request, WellKnownMaskIds.PasswordFolder, cancellationToken);
        if (child is null)
        {
            return Forbid();
        }

        var created = await tree.FindAsync(child.Id, cancellationToken) ?? throw new NotAPasswordStoreItemException();
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(created.ConcurrencyToken);
        return Created(PasswordStoreResourceBuilder.FolderHref(created.Id), await resources.FolderAsync(created, cancellationToken));
    }

    /// <summary>
    /// A new Password Entry, still without content: its <c>versions</c> rel takes the sealed bytes like any first version
    /// (POST, PUT to the upload URL, PUT the version), and the finalize refuses bytes that are not an envelope. Refused
    /// before anything is written when the caller could not have the entry sealed honestly (ADR 0982): the same rule
    /// that withholds the <c>password-entries</c> rel.
    /// </summary>
    [HttpPost("entries")]
    public async Task<IActionResult> CreateEntry(Guid folderId, [FromBody] CreatePasswordItemRequest request, CancellationToken cancellationToken)
    {
        var child = await CreateAsync(folderId, request, WellKnownMaskIds.PasswordEntry, cancellationToken,
            beforeWrite: folder => entryCreation.RequireAsync(folder.StoreId, cancellationToken));
        if (child is null)
        {
            return Forbid();
        }

        var created = await tree.FindAsync(child.Id, cancellationToken) ?? throw new NotAPasswordStoreItemException();
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(created.ConcurrencyToken);
        return Created(PasswordStoreResourceBuilder.EntryHref(created.Id), await resources.EntryAsync(created, cancellationToken));
    }

    // The one create both kinds share: a document of the store's mask, by name, under this store or folder. What differs
    // per kind before the write (an entry's sealing check, ADR 0982) is passed in.
    private async Task<Document?> CreateAsync(
        Guid folderId, CreatePasswordItemRequest request, Guid maskId, CancellationToken cancellationToken, Func<PasswordItem, Task>? beforeWrite = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new PasswordItemNameRequiredException();
        }

        var folder = await VisibleFolderAsync(folderId, cancellationToken);
        if (!(await access.GetCallerRightsAsync(folder.Id, cancellationToken)).CanCreateSubItems)
        {
            return null;
        }

        if (beforeWrite is not null)
        {
            await beforeWrite(folder);
        }

        var tenantId = await dbContext.Documents.Where(d => d.Id == folder.Id).Select(d => d.TenantId).SingleAsync(cancellationToken);
        var (userId, serviceAccountId) = access.GetCallerIdentity();
        var child = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = folder.Id,
            Name = request.Name.Trim(),
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, maskId, cancellationToken)
                ?? throw new InvalidOperationException($"Tenant {tenantId} has no mask {maskId}; the well-known masks were not seeded."),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(child);

        try
        {
            await dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (DocumentNameNotUniqueException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await queue.EnqueueAsync(child.Id, cancellationToken);
        await audit.RecordAsync(AuditActions.DocumentCreated, "Document", child.Id, child.Name, cancellationToken: cancellationToken);
        return child;
    }

    /// <summary>
    /// Every folder and entry beneath this store or folder, at any depth, as a cursor-paginated page (ADR 0207) —
    /// the flat tree apass's completion, <c>ls</c> and <c>find</c> read instead of walking folder by folder.
    /// </summary>
    /// <remarks>
    /// Rows the caller may not see are omitted, exactly as the folder listing omits them; the page itself advances
    /// by what was fetched, so an omitted row never stalls the cursor.
    /// </remarks>
    [HttpGet("descendants")]
    public async Task<IActionResult> Descendants(Guid folderId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var folder = await VisibleFolderAsync(folderId, cancellationToken);
        var pageSize = PageSize.Resolve(limit);
        var ids = await tree.DescendantIdsAsync(folder, cancellationToken);

        var rows = dbContext.Documents
            .Where(d => ids.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.ParentId,
                d.CreatedAt,
                MaskId = dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault(),
            });

        if (Cursor.TryDecode(cursor, out var cursorAt, out var cursorId))
        {
            rows = rows.Where(d => d.CreatedAt > cursorAt || (d.CreatedAt == cursorAt && d.Id > cursorId));
        }

        var fetched = await rows
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var rights = await access.GetCallerRightsForManyAsync([.. page.Select(r => r.Id)], cancellationToken);
        // The autofill metadata for the page in ONE query (ADR 0973): a browser host matches a site against these
        // without opening a single entry, which is what makes an autofill sheet possible without the key.
        var index = await PasswordIndexValues.LoadAsync(dbContext, [.. page.Select(r => r.Id)], cancellationToken);
        var resource = new PasswordDescendantsResource
        {
            Items = [.. page
                .Where(r => rights.TryGetValue(r.Id, out var right) && right.CanSee)
                .Select(r =>
                {
                    var isFolder = PasswordStoreTree.KindOf(r.MaskId) == PasswordItemKind.Folder;
                    var row = new PasswordDescendantResource
                    {
                        Id = r.Id,
                        Name = r.Name,
                        // Descendants of a store or folder always have a parent; the store itself is the floor.
                        ParentId = r.ParentId ?? folder.Id,
                        Kind = isFolder ? "folder" : "entry",
                        Site = index.GetValueOrDefault(r.Id)?.Site,
                        UserName = index.GetValueOrDefault(r.Id)?.UserName,
                        App = index.GetValueOrDefault(r.Id)?.App,
                        HasCode = index.GetValueOrDefault(r.Id)?.HasCode ?? false,
                    };
                    row.Links.Add(new Link("self",
                        isFolder ? PasswordStoreResourceBuilder.FolderHref(r.Id) : PasswordStoreResourceBuilder.EntryHref(r.Id), "GET"));
                    return row;
                })],
        };

        resource.Links.Add(new Link("self", $"/api/password-folders/{folder.Id}/descendants", "GET"));
        if (hasMore)
        {
            resource.Links.Add(new Link("next",
                $"/api/password-folders/{folder.Id}/descendants?cursor={Cursor.Encode(page[^1].CreatedAt, page[^1].Id)}&limit={pageSize}", "GET"));
        }

        return Ok(resource);
    }

    [HttpHead("descendants")]
    public async Task<IActionResult> DescendantsHead(Guid folderId, CancellationToken cancellationToken)
    {
        await VisibleFolderAsync(folderId, cancellationToken);
        return NoContent();
    }

    private async Task<PasswordItem> VisibleFolderAsync(Guid folderId, CancellationToken cancellationToken) =>
        await tree.FindAsync(folderId, cancellationToken) is { Kind: PasswordItemKind.Store or PasswordItemKind.Folder } folder
        && (await access.GetCallerRightsAsync(folder.Id, cancellationToken)).CanSee
            ? folder
            : throw new NotAPasswordStoreItemException();
}
