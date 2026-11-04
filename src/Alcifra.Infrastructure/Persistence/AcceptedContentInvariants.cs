using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// A mask that names the file types it holds gets only those (ABI 1.29, ADR 1046). Here because content is filed by the
/// workbench, the tool, WebDAV, import and the module facade, and a check in any one of them is a check the others skip:
/// each of them adds a version row, and the object key that row carries is what the content really is. A document name
/// proves nothing, since a rename leaves the bytes as they were.
/// </summary>
/// <remarks>
/// Two moments can break the rule: content arrives under a mask, or a mask arrives over content. Every other save costs one
/// dictionary check while no module declares anything, and a query only for a version or a mask change.
/// </remarks>
public static class AcceptedContentInvariants
{
    public static async Task EnforceAsync(AlcifraDbContext dbContext, IModuleMaskDeclarations? masks, CancellationToken cancellationToken)
    {
        if (masks is not { AcceptedContentOf.Count: > 0 })
        {
            return;
        }

        var filed = dbContext.ChangeTracker.Entries<DocumentVersion>()
            .Where(e => e.State == EntityState.Added || (e.State == EntityState.Modified && e.Property(v => v.ObjectKey).IsModified))
            .Select(e => e.Entity)
            .ToList();
        foreach (var version in filed)
        {
            if (await AcceptedByDocumentAsync(dbContext, masks, version.DocumentId, cancellationToken) is { } accepted)
            {
                Refuse(accepted, version.ObjectKey);
            }
        }

        var remasked = dbContext.ChangeTracker.Entries<Document>()
            .Where(e => e.State == EntityState.Modified && e.Property(d => d.MaskVersionId).IsModified)
            .Select(e => e.Entity)
            .ToList();
        foreach (var document in remasked)
        {
            if (await AcceptedByMaskVersionAsync(dbContext, masks, document.MaskVersionId, cancellationToken) is not { } accepted)
            {
                continue;
            }

            var documentId = document.Id;
            var keys = await dbContext.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
                .Where(v => v.DocumentId == documentId).Select(v => v.ObjectKey).ToListAsync(cancellationToken);
            foreach (var key in keys)
            {
                Refuse(accepted, key);
            }
        }
    }

    private static void Refuse(AcceptedContent accepted, string objectKey)
    {
        if (!accepted.Accepts(objectKey))
        {
            throw new ContentExtensionNotAcceptedException(accepted.MaskName, Path.GetExtension(objectKey), accepted.Extensions);
        }
    }

    private static async Task<AcceptedContent?> AcceptedByDocumentAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations masks, Guid documentId, CancellationToken cancellationToken)
    {
        // The tracked document first: it may be new in this very save, or be changing its mask in it.
        var tracked = dbContext.ChangeTracker.Entries<Document>().FirstOrDefault(e => e.Entity.Id == documentId)?.Entity;
        Guid? maskVersionId = tracked is not null
            ? tracked.MaskVersionId
            : await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
                .Where(d => d.Id == documentId).Select(d => (Guid?)d.MaskVersionId).FirstOrDefaultAsync(cancellationToken);
        return await AcceptedByMaskVersionAsync(dbContext, masks, maskVersionId, cancellationToken);
    }

    private static async Task<AcceptedContent?> AcceptedByMaskVersionAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations masks, Guid? maskVersionId, CancellationToken cancellationToken)
    {
        if (maskVersionId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        var maskId = await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.Id == id).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
        return maskId is { } mask ? masks.AcceptedContentOf.GetValueOrDefault(mask) : null;
    }
}
