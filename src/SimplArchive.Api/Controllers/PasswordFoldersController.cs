using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;
using SimplArchive.Api.PasswordStores;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A password store or a folder in one (ADR 0964): its contents, and the creation of folders and entries inside it.
/// Rename, move and delete are the document's own doors (<c>document</c> and <c>move</c> rels on every row).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/password-folders/{folderId:guid}")]
[Authorize]
public class PasswordFoldersController(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    PasswordStoreTree tree,
    PasswordStoreResourceBuilder resources,
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
    /// (POST, PUT to the upload URL, PUT the version), and the finalize refuses bytes that are not an envelope.
    /// </summary>
    [HttpPost("entries")]
    public async Task<IActionResult> CreateEntry(Guid folderId, [FromBody] CreatePasswordItemRequest request, CancellationToken cancellationToken)
    {
        var child = await CreateAsync(folderId, request, WellKnownMaskIds.PasswordEntry, cancellationToken);
        if (child is null)
        {
            return Forbid();
        }

        var created = await tree.FindAsync(child.Id, cancellationToken) ?? throw new NotAPasswordStoreItemException();
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(created.ConcurrencyToken);
        return Created(PasswordStoreResourceBuilder.EntryHref(created.Id), await resources.EntryAsync(created, cancellationToken));
    }

    // The one create both kinds share: a document of the store's mask, by name, under this store or folder.
    private async Task<Document?> CreateAsync(Guid folderId, CreatePasswordItemRequest request, Guid maskId, CancellationToken cancellationToken)
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

    private async Task<PasswordItem> VisibleFolderAsync(Guid folderId, CancellationToken cancellationToken) =>
        await tree.FindAsync(folderId, cancellationToken) is { Kind: PasswordItemKind.Store or PasswordItemKind.Folder } folder
        && (await access.GetCallerRightsAsync(folder.Id, cancellationToken)).CanSee
            ? folder
            : throw new NotAPasswordStoreItemException();
}
