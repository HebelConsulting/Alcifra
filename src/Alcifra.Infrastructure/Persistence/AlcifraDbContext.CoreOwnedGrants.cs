namespace Alcifra.Infrastructure.Persistence;

// The core's own WRITE GRANTS, split from the context itself (the 1000-line rule): each one names a single document a
// core-owned invariant would otherwise refuse, so a grant cannot leak to an unrelated write in the same unit of work.
// See CoreOwnedDocumentInvariants for why a grant names a document rather than switching the rule off.
public partial class AlcifraDbContext
{
    // The documents the core's own filing path may give a core-owned mask in this unit of work (ADR 0891).
    private readonly HashSet<Guid> _coreOwnedMaskGrants = [];

    /// <summary>Allows <paramref name="documentId"/> to be given a core-owned mask (ADR 0891). Only the core's manual
    /// filing calls this; every other path that assigns such a mask is refused by SaveChanges.</summary>
    public void PermitCoreOwnedMask(Guid documentId) => _coreOwnedMaskGrants.Add(documentId);

    internal bool IsCoreOwnedMaskPermitted(Guid documentId) => _coreOwnedMaskGrants.Contains(documentId);

    private readonly HashSet<Guid> _coreOwnedReferenceGrants = [];

    /// <summary>Allows the reference <paramref name="referenceId"/> to be placed in, or removed from, a core-owned folder
    /// (ADR 1066). Only the core's own reconciliation calls this; every other such write is refused by SaveChanges.</summary>
    public void PermitCoreOwnedReference(Guid referenceId) => _coreOwnedReferenceGrants.Add(referenceId);

    // A grant is spent by the write it was given for: the same context may go on to other saves (the startup reconciliation
    // does), and a reference once placed by the core must not stay removable through the grant that placed it.
    internal bool TakeCoreOwnedReferenceGrant(Guid referenceId) => _coreOwnedReferenceGrants.Remove(referenceId);

    private readonly HashSet<Guid> _coreOwnedFolderMoveGrants = [];

    /// <summary>Allows <paramref name="documentId"/>, a protected core-owned FOLDER, to be moved (ADR 1095). Only the core's
    /// own act calls this — choosing another standard repository moves the container with it, in that change's
    /// transaction; every other move of a protected folder is refused by SaveChanges.</summary>
    public void PermitCoreOwnedFolderMove(Guid documentId) => _coreOwnedFolderMoveGrants.Add(documentId);

    // Spent by the move it was given for, like the reference grant: the container moves once with the setting, and the
    // grant must not stay behind to allow a second, unrelated move of the same save.
    internal bool TakeCoreOwnedFolderMoveGrant(Guid documentId) => _coreOwnedFolderMoveGrants.Remove(documentId);

    private readonly HashSet<Guid> _coreOwnedFolderRenameGrants = [];

    /// <summary>Allows <paramref name="documentId"/>, a protected core-owned FOLDER, to be renamed (ADR 1095). Only the
    /// core's own heal calls this — the container carries the product's name (ADR 0986) and follows it; every other
    /// rename of a protected folder is refused by SaveChanges.</summary>
    public void PermitCoreOwnedFolderRename(Guid documentId) => _coreOwnedFolderRenameGrants.Add(documentId);

    // Spent by the rename it was given for, like its two siblings above.
    internal bool TakeCoreOwnedFolderRenameGrant(Guid documentId) => _coreOwnedFolderRenameGrants.Remove(documentId);
}
