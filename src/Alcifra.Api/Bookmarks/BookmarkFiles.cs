using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Controllers;
using Alcifra.Api.Documents;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Bookmarks;

/// <summary>One bookmark file as a browser's sync sees it.</summary>
/// <param name="IsPersonal">Whether it is the file in the caller's own personal space.</param>
/// <param name="CanEdit">Whether the caller's changes are written back; otherwise it is synced read-only.</param>
/// <param name="CurrentVersionId">The version a read follows; null for a file nothing has been written to yet.</param>
public sealed record BookmarkFile(Guid Id, string Name, string FolderName, bool IsPersonal, bool CanEdit, Guid? CurrentVersionId, Guid ConcurrencyToken);

/// <summary>
/// The bookmark files a user's browser keeps in step with (ADR 1062): found by the core's Bookmarks mask, never by name. A
/// file's rights say who gets it, as any document's do. Another person's personal space stays out, administrators included:
/// an administrator may open a space, but a browser must not start syncing it uninvited.
/// </summary>
public sealed class BookmarkFiles(
    AlcifraDbContext dbContext,
    DocumentAccessService access,
    PersonalRepositoryProvisioner personalSpaces,
    IAuditRecorder audit,
    ILogger<BookmarkFiles> logger)
{
    /// <summary>The name of a personal file; one the person already gave something else takes a number.</summary>
    public const string PersonalFileName = "Bookmarks";

    /// <summary>The files the caller may read: their own first, then the shared ones by folder and name.</summary>
    public async Task<IReadOnlyList<BookmarkFile>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var worn = await dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Bookmarks))
            .Where(d => d.PersonalRootOwnerId == null || d.PersonalRootOwnerId == userId)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.ConcurrencyToken,
                d.PersonalRootOwnerId,
                d.CurrentVersionId,
                FolderName = dbContext.Documents.Where(p => p.Id == d.ParentId).Select(p => p.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var rights = await access.GetCallerRightsForManyAsync([.. worn.Select(d => d.Id)], cancellationToken);
        var files = new List<BookmarkFile>();
        foreach (var d in worn.Where(d => rights.TryGetValue(d.Id, out var r) && r.CanSee && r.CanReadContent))
        {
            var current = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, d.Id, d.CurrentVersionId, cancellationToken);
            files.Add(new BookmarkFile(d.Id, d.Name, d.FolderName ?? string.Empty, d.PersonalRootOwnerId == userId, rights[d.Id].CanEditContent, current?.Id, d.ConcurrencyToken));
        }

        return [.. files.OrderByDescending(f => f.IsPersonal).ThenBy(f => f.FolderName, StringComparer.CurrentCulture).ThenBy(f => f.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>The caller's personal file, made at their first sync rather than with every personal space, so a space that
    /// never syncs costs nothing and the provisioning on every WebDAV request stays as it is.</summary>
    public async Task<(BookmarkFile File, bool Created)> EnsurePersonalAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        if ((await ListAsync(userId, cancellationToken)).FirstOrDefault(f => f.IsPersonal) is { } existing)
        {
            return (existing, false);
        }

        // The first level of a personal space is closed (#634), so the file lives in My Documents.
        var root = await personalSpaces.EnsureAsync(userId, tenantId, cancellationToken);
        var folder = await dbContext.Documents
            .Where(d => d.ParentId == root.Id && dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.MyDocuments))
            .Select(d => new { d.Id, d.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"The personal space {root.Id} has no My Documents folder, which provisioning just ensured.");
        var taken = await dbContext.Documents
            .Where(d => d.ParentId == folder.Id && d.Name.StartsWith(PersonalFileName))
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        var name = taken.Contains(PersonalFileName)
            ? Enumerable.Range(2, taken.Count).Select(n => $"{PersonalFileName} ({n})").First(n => !taken.Contains(n))
            : PersonalFileName;
        var maskVersionId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Bookmarks && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} has no Bookmarks mask; the well-known seed has not run.");
        var file = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = folder.Id,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(file);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException and not PersonalSpaceStructureException)
        {
            // Another browser's first sync won the sibling name: the winner's file is the file.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(e, "Creating the bookmark file for user {UserId} lost a race; using the one that won", userId);
            return ((await ListAsync(userId, cancellationToken)).FirstOrDefault(f => f.IsPersonal)
                ?? throw new InvalidOperationException($"The bookmark file of user {userId} could neither be created nor found."), false);
        }

        await audit.RecordAsync(AuditActions.BookmarkFileCreated, "Document", file.Id, file.Name, "Personal bookmark file", cancellationToken: cancellationToken);
        logger.LogInformation("Created the bookmark file {DocumentId} for user {UserId}", file.Id, userId);
        return (new BookmarkFile(file.Id, file.Name, folder.Name, true, true, null, file.ConcurrencyToken), true);
    }
}
