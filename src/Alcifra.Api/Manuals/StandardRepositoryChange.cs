using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Manuals;

/// <summary>
/// What changing the tenant's standard repository does beyond the setting itself (ADRs 0892/1095): the core's container
/// folder — and with it the manuals folder and the Installation folder — moves with it. Runs INSIDE the tenant-settings
/// PUT's verb contract, so it commits with the setting or not at all, under the precondition the administrator sent for
/// the tenant — never on its own.
/// </summary>
public static class StandardRepositoryChange
{
    /// <summary>
    /// Moves the core's container folder directly under <paramref name="standardRepositoryId"/>, if there is one and it
    /// is not already there; a tenant whose container does not exist yet has its manuals folder moved instead (the next
    /// start nests it). Does not save — the caller commits it together with the change of standard repository.
    /// </summary>
    public static async Task MoveUnderAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid standardRepositoryId, CancellationToken cancellationToken)
    {
        if (await CoreFolder.FindAsync(dbContext, cancellationToken) is { } container)
        {
            if (container.ParentId != standardRepositoryId)
            {
                // The one move a protected core-owned folder makes (ADR 1095): it travels with the setting that
                // chooses the repository, and the grants are spent by this save. Taking the next free name there is
                // part of the same move, so it asks for the rename grant too.
                dbContext.PermitCoreOwnedFolderMove(container.Id);
                var name = await CoreOwnedFolders.FreeNameAsync(dbContext, tenantId, standardRepositoryId, container.Name, cancellationToken);
                if (!string.Equals(name, container.Name, StringComparison.Ordinal))
                {
                    dbContext.PermitCoreOwnedFolderRename(container.Id);
                }

                container.ParentId = standardRepositoryId;
                container.Name = name;
            }

            return;
        }

        if (await ManualsFolder.FindAsync(dbContext, cancellationToken) is { } folder && folder.ParentId != standardRepositoryId)
        {
            folder.ParentId = standardRepositoryId;
            folder.Name = await CoreOwnedFolders.FreeNameAsync(dbContext, tenantId, standardRepositoryId, folder.Name, cancellationToken);
        }
    }
}
