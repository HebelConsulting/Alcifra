using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>
/// The folders active modules ask for in every user's personal space (ABI 1.18, ADR 0937), such as the Messenger's
/// "My Messages": created beside My Documents, and the module's principal granted rights on THAT folder only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only that folder.</b> A personal space is its owner's; the module needs a place in it, not a view of it. Its access
/// entry sits on its own folder, which inherits the owner's rights as every folder there does, so the owner sees what the
/// module files and the module sees nothing else of the space.
/// </para>
/// <para>
/// <b>Three triggers, one idempotent step:</b> provisioning a personal space (a new user), activating the module
/// (everyone already there), and every start (a module that started declaring one in a later release).
/// </para>
/// </remarks>
public sealed class ModulePersonalFolders(AlcifraDbContext dbContext, IModuleMaskDeclarations declarations)
{
    /// <summary>Ensures the active modules' folders inside <paramref name="root"/>, the user's personal root.</summary>
    public async Task EnsureAsync(Document root, Guid tenantId, CancellationToken cancellationToken)
    {
        if (declarations.ProvisionedInPersonalSpace.Count == 0)
        {
            return;
        }

        var active = await ModuleActivationCheck.ActiveIdsAsync(
            dbContext, [.. declarations.ProvisionedInPersonalSpace.Select(f => f.ModuleId).Distinct()], DateTimeOffset.UtcNow, cancellationToken);
        foreach (var folder in declarations.ProvisionedInPersonalSpace.Where(f => active.Contains(f.ModuleId)))
        {
            await EnsureFolderAsync(root, tenantId, folder, cancellationToken);
        }
    }

    /// <summary>Ensures them in every personal space of the tenant (activation, startup).</summary>
    public async Task EnsureForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (declarations.ProvisionedInPersonalSpace.Count == 0)
        {
            return;
        }

        var roots = await dbContext.Documents.Where(d => d.ParentId == null && d.PersonalOfUserId != null).ToListAsync(cancellationToken);
        foreach (var root in roots)
        {
            await EnsureAsync(root, tenantId, cancellationToken);
        }
    }

    private async Task EnsureFolderAsync(Document root, Guid tenantId, ProvisionedFolder folder, CancellationToken cancellationToken)
    {
        var maskVersionId = await dbContext.MaskVersions
            .Where(v => v.MaskId == folder.MaskId && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (maskVersionId is null)
        {
            return;   // the module's masks are seeded on activation; nothing to wear yet
        }

        var existing = await dbContext.Documents
            .Where(d => d.ParentId == root.Id && dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == folder.MaskId))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            existing = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = root.Id,
                Name = folder.Name,
                MaskVersionId = maskVersionId.Value,
                CreatedByUserId = root.PersonalOfUserId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            dbContext.Documents.Add(existing);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (await ModulePrincipal.FindAsync(dbContext, folder.ModuleId, cancellationToken) is not { } principal
            || await dbContext.AclEntries.AnyAsync(e => e.DocumentId == existing.Id && e.ServiceAccountId == principal.Id, cancellationToken))
        {
            return;
        }

        // What the module does in its folder: file into it, read it, rename and remove what it filed. Never manage its
        // permissions: who sees the owner's space is the owner's business.
        dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = existing.Id,
            ServiceAccountId = principal.Id,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanCreateSubItems = true,
            CanDelete = true,
            CanMove = true,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Every tenant with an active module that provisions a folder: run at startup, after the masks are healed.</summary>
    public static async Task EnsureEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        var declarations = root.GetRequiredService<IModuleMaskDeclarations>();
        if (declarations.ProvisionedInPersonalSpace.Count == 0)
        {
            return;
        }

        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(cancellationToken);
        }

        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger<ModulePersonalFolders>();
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = root.CreateScope();
                ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
                await scope.ServiceProvider.GetRequiredService<ModulePersonalFolders>().EnsureForTenantAsync(tenantId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Provisioning module folders in the personal spaces of tenant {TenantId} failed; the next start tries again", tenantId);
            }
        }
    }
}
