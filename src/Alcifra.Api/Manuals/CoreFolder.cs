using Alcifra.Localization;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Manuals;

/// <summary>
/// The core's own CONTAINER folder (ADR 1095): one per tenant, named after the product, found by its core-owned mask
/// and never by name. It lives directly under the standard repository (ADR 0892), moves with it, and holds the manuals
/// folder and the Installation folder — nothing else.
/// </summary>
/// <remarks>
/// It is the parent of the two, so a person moving either out does not break the container: the manuals folder is
/// deliberately not protected (a person may rename or move it), the Installation folder is. The container itself is
/// protected; the two changes the core makes to it — moving with the standard repository, following a product
/// rename — travel through explicit grants (ADR 1066's mechanism).
/// </remarks>
public static class CoreFolder
{
    /// <summary>The folder's name: the product's (ADR 0986), "Alcifra" unless the installation names itself.</summary>
    public static string Name => CoreOwnedNames.CoreFolder;

    /// <summary>The live folder wearing the container mask in the current tenant, or null.</summary>
    public static Task<Document?> FindAsync(AlcifraDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.CoreFolder))
            // By id, not by date: there is one live folder per mask and tenant (the core-owned rule), and the SQLite
            // test provider cannot order by a DateTimeOffset.
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The container, created directly under the standard repository when there is none; null (and a Warning) when the
    /// tenant has no standard repository to create it in.
    /// </summary>
    public static async Task<Document?> FindOrCreateAsync(
        AlcifraDbContext dbContext, Guid tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, cancellationToken) is { } existing)
        {
            return existing;
        }

        var standardId = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.StandardRepositoryId)
            .SingleOrDefaultAsync(cancellationToken);
        var standard = standardId is { } id
            ? await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == id, cancellationToken)
            : null;
        if (standard is null)
        {
            logger.LogWarning(
                "Tenant {TenantId} has no standard repository, so its core folder was not created. A tenant "
                + "administrator can choose one in the tenant settings; the folder is created at the next start.", tenantId);
            return null;
        }

        var author = (standard.CreatedByUserId, standard.CreatedByUserId is null ? standard.CreatedByServiceAccountId : null);
        return await CoreOwnedFolders.CreateAsync(
            dbContext, tenantId, standard.Id, Name, WellKnownMaskIds.CoreFolder, author, logger, cancellationToken);
    }
}
