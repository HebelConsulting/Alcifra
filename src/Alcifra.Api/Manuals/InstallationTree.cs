using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Manuals;

/// <summary>
/// The core's own tree in every tenant (ADR 1095, #1893), healed idempotently at startup: the container folder
/// (<see cref="CoreFolder"/>) directly under the standard repository, the manuals folder inside it, and the
/// <c>Installation</c> folder beside the manuals — with the archive signing keys (ADR 0961) moved into it and wearing
/// the <see cref="WellKnownMaskIds.PublicKeys"/> mask.
/// </summary>
/// <remarks>
/// <para>
/// <b>No column records that the migration ran: the found shape IS the marker.</b> Every step is "if the old default
/// shape is still here, make it the new one": the container is found or created, a manuals folder that still sits
/// directly under the standard repository moves inside, the Installation folder is found or created, and a signing-keys
/// document still wearing the Manual mask is moved and re-masked. Run again, every step finds its own result and does
/// nothing.
/// </para>
/// <para>
/// <b>What a person did is not undone.</b> Only the pre-container DEFAULT shape moves: once the manuals folder is
/// inside the container, a person who moves it out again keeps it where they put it (it is deliberately not protected)
/// — the next start does not drag it back.
/// </para>
/// <para>
/// <b>Safe across two instances</b>: the whole step runs in one transaction that FIRST locks the tenant's row, the
/// posture <see cref="ManualFiler"/> takes — a sibling instance starting at the same moment waits, then reads what
/// this one committed.
/// </para>
/// </remarks>
public static class InstallationTree
{
    /// <summary>The folder's fixed name (ADR 1095) — "Installation".</summary>
    public static string Name => Alcifra.Localization.CoreOwnedNames.InstallationFolder;

    /// <summary>The live Installation folder in the current tenant, or null.</summary>
    public static Task<Document?> FindAsync(AlcifraDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Installation))
            // By id, not by date: there is one live folder per mask and tenant (the core-owned rule), and the SQLite
            // test provider cannot order by a DateTimeOffset.
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The Installation folder, created inside the container when there is none; null (and a Warning) when the tenant
    /// has no standard repository (so no container) to create it in.
    /// </summary>
    public static async Task<Document?> FindOrCreateAsync(
        AlcifraDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, cancellationToken) is { } existing)
        {
            return existing;
        }

        return await CoreFolder.FindOrCreateAsync(dbContext, tenantId, logger, cancellationToken) is { } container
            ? await CreateAsync(dbContext, tenantId, container, logger, cancellationToken)
            : null;
    }

    /// <summary>
    /// The startup migration (ADR 1095): the tree's shape, reached from whatever shape the tenant is in. Logs one
    /// Information line for each part of the tree it actually had to create or move, and nothing when the tenant
    /// already has the shape.
    /// </summary>
    public static async Task EnsureAsync(
        AlcifraDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        var owned = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var transaction = owned;

        // THE LOCK, before any read, exactly as the filer takes it: a sibling instance running the same step waits
        // here and then reads what this one committed. TOKEN DELIBERATELY NOT MOVED: nothing changes — the statement
        // exists only for the row lock it takes.
        await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.StandardRepositoryId, t => t.StandardRepositoryId), cancellationToken);

        var standardId = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.StandardRepositoryId)
            .SingleOrDefaultAsync(cancellationToken);
        if (standardId is not { } standard)
        {
            // Nothing to build on; the filings that follow warn with the remedy, and the next start with a standard
            // repository builds the tree.
            return;
        }

        var container = await CoreFolder.FindOrCreateAsync(dbContext, tenantId, logger, cancellationToken);
        if (container is null)
        {
            return;
        }

        // The container follows the standard repository (ADR 0892). The settings PUT already moves it in the change's
        // own transaction; this heals an installation that changed its repository before this step existed.
        if (container.ParentId != standard)
        {
            // The move grant is the protected folder's one permitted move (ADR 1095), and the free-name adjustment
            // it may need asks for the rename grant too; both are spent by this save.
            dbContext.PermitCoreOwnedFolderMove(container.Id);
            var name = await CoreOwnedFolders.FreeNameAsync(dbContext, tenantId, standard, container.Name, cancellationToken);
            if (!string.Equals(name, container.Name, StringComparison.Ordinal))
            {
                dbContext.PermitCoreOwnedFolderRename(container.Id);
            }

            container.ParentId = standard;
            container.Name = name;
            logger.LogInformation("Moved the core folder '{Name}' under the standard repository of tenant {TenantId} (ADR 1095)",
                container.Name, tenantId);
        }

        // The manuals folder in the shape every installation had before the container: directly under the standard
        // repository. Once inside the container, a person's own move is theirs and this never drags it back.
        if (await ManualsFolder.FindAsync(dbContext, cancellationToken) is { } manuals && manuals.ParentId == standard)
        {
            manuals.ParentId = container.Id;
            logger.LogInformation("Moved '{Name}' into the core folder of tenant {TenantId} (ADR 1095)", manuals.Name, tenantId);
        }

        var installation = await FindAsync(dbContext, cancellationToken)
            ?? await CreateAsync(dbContext, tenantId, container, logger, cancellationToken);
        if (installation is null)
        {
            return;
        }

        await ReMaskLegacySigningKeysAsync(dbContext, tenantId, installation, logger, cancellationToken);

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static Task<Document?> CreateAsync(
        AlcifraDbContext dbContext, Guid tenantId, Document container, ILogger logger, CancellationToken cancellationToken)
    {
        var author = (container.CreatedByUserId, container.CreatedByUserId is null ? container.CreatedByServiceAccountId : null);
        return CoreOwnedFolders.CreateAsync(
            dbContext, tenantId, container.Id, Name, WellKnownMaskIds.Installation, author, logger, cancellationToken);
    }

    /// <summary>
    /// The archive signing keys filed before the Installation folder existed (ADR 0961): the document is found by the
    /// Manual "Article number" it carried, moved into the Installation folder and re-masked as a public key, whose
    /// "Purpose" is what the filer finds it by from then on. Its Manual field values are cleared with the mask — they
    /// describe a Manual, and the bytes stay the same.
    /// </summary>
    private static async Task ReMaskLegacySigningKeysAsync(
        AlcifraDbContext dbContext, Guid tenantId, Document installation, ILogger logger, CancellationToken cancellationToken)
    {
        var legacyId = await ManualFiler.ArticleNumberValues(dbContext, ManualFiler.ArchiveSigningKeysArticleNumber)
            .Select(v => (Guid?)v.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (legacyId is not { } documentId)
        {
            return;
        }

        var maskVersionId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.PublicKeys && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var purposeDefinitionId = maskVersionId is { } version
            ? await dbContext.FieldDefinitions
                .Where(f => f.MaskVersionId == version && f.Name == WellKnownMaskIds.PublicKeyFields.Purpose)
                .Select(f => (Guid?)f.Id)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (maskVersionId is not { } target || purposeDefinitionId is not { } purpose)
        {
            logger.LogWarning(
                "Tenant {TenantId} has no Public keys mask yet, so its archive signing keys stay where they are; the next "
                + "start heals it.", tenantId);
            return;
        }

        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        dbContext.FieldValues.RemoveRange(
            await dbContext.FieldValues.Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken));
        dbContext.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            FieldDefinitionId = purpose,
            Value = ManualFiler.ArchiveSigningPurpose,
        });
        document.MaskVersionId = target;
        document.ParentId = installation.Id;
        logger.LogInformation(
            "Moved the archive signing keys into '{Installation}' and re-masked them as a public key (ADR 1095)", installation.Name);
    }
}
