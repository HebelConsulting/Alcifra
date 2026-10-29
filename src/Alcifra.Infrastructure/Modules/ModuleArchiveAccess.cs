using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Acl;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// The facade's ABI 1.18 operations (ADR 0937): a module granting and revoking a user's access to its own documents,
/// purging one, removing a reference, and finding the users it may include. Its own class because the facade is at its
/// size limit; the facade forwards to it.
/// </summary>
/// <remarks>
/// <b>Only documents wearing one of the CALLING module's masks</b> are touched by access and deletion. A module may
/// otherwise see and write what the tenant consented to, but deciding who reads a document, or destroying one, is the
/// archive's business unless the document is the module's own kind. Outside a module call nothing here acts.
/// </remarks>
internal sealed class ModuleArchiveAccess(
    AlcifraDbContext dbContext,
    Func<string?> moduleId,
    Func<string, Guid, Guid, string, string?, CancellationToken, Task> auditWrite,
    IServiceProvider? services,
    ILogger? logger)
{
    public async Task GrantAsync(Guid documentId, Guid userId, ModuleAccess access, CancellationToken cancellationToken)
    {
        var document = await OwnDocumentAsync(documentId, cancellationToken);
        if (!await dbContext.Users.AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken))
        {
            throw new ArgumentException($"User {userId} is not an active user of this tenant.", nameof(userId));
        }

        var entry = await dbContext.AclEntries.FirstOrDefaultAsync(e => e.DocumentId == documentId && e.UserId == userId, cancellationToken);
        if (entry is null)
        {
            entry = new AclEntry { Id = Guid.NewGuid(), TenantId = document.TenantId, DocumentId = documentId, UserId = userId };
            dbContext.AclEntries.Add(entry);
        }

        entry.CanSee = true;
        entry.CanReadContent = true;
        entry.CanCreateSubItems = access == ModuleAccess.Contribute;
        await dbContext.SaveChangesAsync(cancellationToken);
        await ReindexAsync(documentId, cancellationToken);
        await auditWrite(ModuleWriteAudit.AclGranted, document.TenantId, documentId, document.Name, $"user {userId}: {access}", cancellationToken);
    }

    public async Task RevokeAsync(Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        var document = await OwnDocumentAsync(documentId, cancellationToken);
        var entries = await dbContext.AclEntries.Where(e => e.DocumentId == documentId && e.UserId == userId).ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return;
        }

        dbContext.AclEntries.RemoveRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);
        await ReindexAsync(documentId, cancellationToken);
        await auditWrite(ModuleWriteAudit.AclRevoked, document.TenantId, documentId, document.Name, $"user {userId}", cancellationToken);
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await OwnDocumentAsync(documentId, cancellationToken);
        var purger = services?.GetService(typeof(IModuleDocumentPurger)) as IModuleDocumentPurger
            ?? throw new InvalidOperationException("Purging needs the host's purger; this host wires none.");
        var declarations = services?.GetService(typeof(IModuleMaskDeclarations)) as IModuleMaskDeclarations;
        var maskId = await MaskIdAsync(documentId, cancellationToken);

        await purger.PurgeAsync(documentId, respectLegalHold: !(maskId is { } id && declarations?.NotHeld.Contains(id) == true), cancellationToken);
        await auditWrite(ModuleWriteAudit.Purged, document.TenantId, documentId, document.Name, "purged at once (module-owned lifetime)", cancellationToken);
        logger?.LogInformation("Module {ModuleId} purged document {DocumentId}", moduleId(), documentId);
    }

    public async Task<bool> DeleteReferenceAsync(Guid targetDocumentId, Guid folderId, CancellationToken cancellationToken)
    {
        var document = await OwnDocumentAsync(targetDocumentId, cancellationToken);
        var references = await dbContext.DocumentReferences
            .Where(r => r.TargetDocumentId == targetDocumentId && r.ParentFolderId == folderId)
            .ToListAsync(cancellationToken);
        if (references.Count == 0)
        {
            return false;
        }

        dbContext.DocumentReferences.RemoveRange(references);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWrite(ModuleWriteAudit.ReferenceRemoved, document.TenantId, targetDocumentId, document.Name, $"from folder {folderId}", cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ModuleUser>> SearchUsersAsync(string term, int limit, CancellationToken cancellationToken)
    {
        if (moduleId() is null)
        {
            return [];
        }

        // Unrevoked certificates in SQL, the validity window in memory: comparing DateTimeOffset in a predicate throws
        // on SQLite (the tests' provider), and a tenant's enrolled set is small.
        var now = DateTimeOffset.UtcNow;
        var enrolled = await dbContext.EnrolledCertificates.AsNoTracking()
                .Where(c => c.RevokedAt == null)
                .Select(c => new { c.TenantId, c.UserId, c.NotBefore, c.NotAfter, c.AttestationRootThumbprint })
                .ToListAsync(cancellationToken);
        if (enrolled.Count == 0)
        {
            return [];
        }

        // A USABLE key, as the reader answer judges it (ADR 0963): a key not attested to a root trusted here seals nothing.
        var trust = await Certificates.Attestation.AttestationTrust.ForTenantAsync(dbContext, enrolled[0].TenantId, cancellationToken);
        var holders = enrolled
            .Where(c => c.NotBefore <= now && c.NotAfter > now && trust.Addresses(c.AttestationRootThumbprint))
            .Select(c => c.UserId)
            .ToHashSet();
        if (holders.Count == 0)
        {
            return [];
        }

        var needle = term.Trim().ToUpperInvariant();
        var users = await dbContext.Users.AsNoTracking()
            .Where(u => u.IsActive && holders.Contains(u.Id))
            .Where(u => needle.Length == 0 || u.DisplayName.ToUpper().Contains(needle) || u.NormalizedEmail.Contains(needle))
            .OrderBy(u => u.DisplayName)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(u => new ModuleUser(u.Id, u.DisplayName, u.Email))
            .ToListAsync(cancellationToken);
        return users;
    }

    public async Task<ModuleUser?> UserAsync(Guid userId, CancellationToken cancellationToken) =>
        moduleId() is null
            ? null
            : await dbContext.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.IsActive)
                .Select(u => new ModuleUser(u.Id, u.DisplayName, u.Email))
                .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> PersonalSpaceFolderAsync(Guid userId, Guid folderMaskId, CancellationToken cancellationToken)
    {
        // Only a mask of the calling module's own that it asked the core to provision: the answer is "which folder is
        // the module's in this person's space", never a way to look around in it.
        if (moduleId() is not { } id
            || (services?.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>)) as IReadOnlyList<ModuleLoader.LoadedModule>)
                ?.FirstOrDefault(m => m.Module.ModuleId == id) is not { } loaded
            || loaded.Module.Masks.FirstOrDefault(m => m.MaskId == folderMaskId) is not { PersonalSpaceFolderName.Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Module '{moduleId()}' may ask only for a folder of its own mask that it asked the core to provision in personal spaces.");
        }

        return await dbContext.Documents
            .Where(d => dbContext.Documents.Any(r => r.Id == d.ParentId && r.ParentId == null && r.PersonalOfUserId == userId)
                && dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == folderMaskId))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The document, provided it wears one of the calling module's masks; refused otherwise.
    private async Task<Domain.Documents.Document> OwnDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (moduleId() is not { } id
            || (services?.GetService(typeof(IReadOnlyList<ModuleLoader.LoadedModule>)) as IReadOnlyList<ModuleLoader.LoadedModule>)
                ?.FirstOrDefault(m => m.Module.ModuleId == id) is not { } loaded)
        {
            throw new InvalidOperationException("Only a module acting as itself may change access to, or delete, its own documents.");
        }

        var document = await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            ?? throw new ArgumentException($"Document {documentId} does not exist.", nameof(documentId));
        var maskId = await MaskIdAsync(documentId, cancellationToken);
        if (maskId is not { } mask || loaded.Module.Masks.All(m => m.MaskId != mask))
        {
            throw new InvalidOperationException(
                $"Module '{id}' may change access to, or delete, only documents wearing one of its own masks; {documentId} does not.");
        }

        return document;
    }

    private Task<Guid?> MaskIdAsync(Guid documentId, CancellationToken cancellationToken) =>
        dbContext.Documents.Where(d => d.Id == documentId)
            .Select(d => dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);

    // Who may see a document is part of what search stores, so a changed access list re-syncs it.
    private async Task ReindexAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (services?.GetService(typeof(IDocumentIndexQueue)) is IDocumentIndexQueue queue)
        {
            await queue.EnqueueAsync(documentId, cancellationToken);
        }
    }
}
