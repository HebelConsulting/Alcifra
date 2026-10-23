using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.PasswordStores;

/// <summary>
/// Get-or-create a user's <c>My Passwords</c> (ADR 0964): exactly one Password Store in the personal space, beside My
/// Documents, created the first time a client follows the rel. Idempotent, and found by MASK, never by name (ADR 0633),
/// so a store caught mid-heal is still the one.
/// </summary>
public sealed class MyPasswordsProvisioner(
    SimplArchiveDbContext dbContext,
    PersonalRepositoryProvisioner personalSpaces,
    IAuditRecorder audit,
    ILogger<MyPasswordsProvisioner> logger)
{
    /// <summary>The store's id, and whether this call created it.</summary>
    public async Task<(Guid StoreId, bool Created)> EnsureAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var root = await personalSpaces.EnsureAsync(userId, tenantId, cancellationToken);
        if (await FindAsync(root.Id, cancellationToken) is { } existing)
        {
            return (existing, false);
        }

        var maskVersionId = await FolderMask.CurrentVersionIdAsync(dbContext, tenantId, WellKnownMaskIds.PasswordStore, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} has no Password Store mask; the well-known masks were not seeded.");
        var store = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = root.Id,
            Name = PersonalFolders.MyPasswords,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(store);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            // A concurrent first use won the sibling name or the one-per-space cap: the winner's store is the answer.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(e, "Creating My Passwords for user {UserId} lost a race; using the store that won", userId);
            return (await FindAsync(root.Id, cancellationToken)
                ?? throw new InvalidOperationException($"My Passwords for user {userId} could neither be created nor found."), false);
        }

        await audit.RecordAsync(AuditActions.PasswordStoreCreated, "Document", store.Id, store.Name, "My Passwords", cancellationToken: cancellationToken);
        logger.LogInformation("Created My Passwords {StoreId} for user {UserId}", store.Id, userId);
        return (store.Id, true);
    }

    private Task<Guid?> FindAsync(Guid personalRootId, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => d.ParentId == personalRootId
                && dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.PasswordStore))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
