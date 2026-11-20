using Alcifra.Localization;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Manuals;

/// <summary>
/// The tenant's manuals folder (ADRs 0891/1095): found by its MASK, created lazily inside the core's own container
/// folder (<see cref="CoreFolder"/>) under the standard repository (ADR 0892), and moved with the container when the
/// standard repository changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by mask, never by name.</b> An administrator may rename the folder or move it; it is still the one
/// wearing "Alcifra Documentation", and SaveChanges guarantees there is at most one live such folder per tenant
/// and that only the core puts the mask on it.
/// </para>
/// <para>
/// <b>A name a user already took is not ours to take.</b> If the container already holds a document called "Manuals"
/// that is not the manuals folder, the folder is created as "Manuals (2)" (then 3, …) and a Warning names both —
/// adopting the user's folder by its name would type somebody's own content as core-owned, and refusing would leave
/// the tenant without manuals over a coincidence of naming.
/// </para>
/// </remarks>
public static class ManualsFolder
{
    /// <summary>The folder's fixed name (ADR 1095), "Manuals" — it no longer carries the product's name.</summary>
    public static string Name => CoreOwnedNames.ManualsFolder;

    /// <summary>The live folder wearing the Documentation mask in the current tenant, or null.</summary>
    public static Task<Document?> FindAsync(AlcifraDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.Documentation))
            // By id, not by date: there is one live folder per mask and tenant (the core-owned rule), and the SQLite
            // test provider cannot order by a DateTimeOffset.
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The manuals folder, created inside the core's container when there is none; null (and a Warning) when the
    /// tenant has no standard repository (so no container) to create it in.
    /// </summary>
    public static async Task<Document?> FindOrCreateAsync(
        AlcifraDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (await CoreFolder.FindOrCreateAsync(dbContext, tenantId, logger, cancellationToken) is not { } container)
        {
            return null;
        }

        var maskVersionId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Documentation && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (maskVersionId is null)
        {
            logger.LogWarning("Tenant {TenantId} has no '{Mask}' mask yet, so no manual was filed; the next start heals it.",
                tenantId, CoreOwnedNames.DocumentationMask);
            return null;
        }

        var name = await CoreOwnedFolders.FreeNameAsync(dbContext, tenantId, container.Id, Name, cancellationToken);
        if (!string.Equals(name, Name, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Tenant {TenantId}: the core folder already holds a document named '{Taken}' that is not the "
                + "manuals folder, so the manuals folder was created as '{Name}'. Rename either as you prefer — the "
                + "manuals folder is recognised by its type, not its name.", tenantId, Name, name);
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = container.Id,
            Name = name,
            MaskVersionId = maskVersionId.Value,
            // The container's own author: the core files on the tenant's behalf, and a document needs a principal
            // (the exactly-one-creator CHECK).
            CreatedByUserId = container.CreatedByUserId,
            CreatedByServiceAccountId = container.CreatedByUserId is null ? container.CreatedByServiceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PermitCoreOwnedMask(folder.Id);
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);
        return folder;
    }

    /// <summary>
    /// The core-owned names follow the product's (ADR 0986), and the manuals folder and the core manual never carry
    /// an earlier default (ADR 1095): the container, the manuals folder when <paramref name="manual"/> is in it, and
    /// the core manual are renamed when they still carry an earlier default ("Alcifra", "Alcifra Manuals", "Alcifra
    /// Manual", or the build's name after the installation named itself), never when a person renamed them, and only
    /// when the present name is free among their siblings. Run under the filer's lock, so two instances never both
    /// rename. The container is protected from RENAMES (ADR 1066's rule); this one is the core's own act and travels
    /// through the same explicit grant a core-owned mask does.
    /// </summary>
    /// <returns>Whether anything was renamed (and saved).</returns>
    public static async Task<bool> HealNamesAsync(
        AlcifraDbContext dbContext, Document? manual, string manualName, ILogger logger, CancellationToken cancellationToken)
    {
        var renamed = false;
        if (await CoreFolder.FindAsync(dbContext, cancellationToken) is { } container
            && CoreOwnedNames.IsEarlierDefault(container.Name, string.Empty)
            && await RenameIfFreeAsync(dbContext, container, CoreFolder.Name, logger, cancellationToken))
        {
            // The container is protected from RENAMES (ADR 1066's rule); this one is the core's own act — the name
            // follows the product (ADR 0986) — and travels through the same explicit grant a core-owned mask does. The
            // grant is given only when the name actually changed, so it never sits unspent on an unrelated save.
            dbContext.PermitCoreOwnedFolderRename(container.Id);
            renamed = true;
        }

        if (await FindAsync(dbContext, cancellationToken) is { } folder
            && CoreOwnedNames.IsEarlierManualsFolderName(folder.Name))
        {
            renamed |= await RenameIfFreeAsync(dbContext, folder, Name, logger, cancellationToken);
        }

        if (manual is not null && string.Equals(manualName, CoreOwnedNames.CoreManual, StringComparison.Ordinal)
            && CoreOwnedNames.IsEarlierDefault(manual.Name, CoreOwnedNames.ManualSuffix))
        {
            renamed |= await RenameIfFreeAsync(dbContext, manual, manualName, logger, cancellationToken);
        }

        if (renamed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return renamed;
    }

    private static async Task<bool> RenameIfFreeAsync(
        AlcifraDbContext dbContext, Document document, string name, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.Documents.AnyAsync(d => d.ParentId == document.ParentId && d.Id != document.Id && d.Name == name, cancellationToken))
        {
            logger.LogWarning("'{Old}' keeps its name: a sibling is already called '{New}' (ADR 0986).", document.Name, name);
            return false;
        }

        logger.LogInformation("Renamed the core-owned '{Old}' to '{New}' (ADRs 0986/1095)", document.Name, name);
        document.Name = name;
        return true;
    }
}
