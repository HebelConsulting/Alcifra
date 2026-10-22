namespace SimplArchive.Api.Errors.Exceptions.OfflineArchives;

/// <summary>
/// The tenant refuses plaintext doors (ADR 0834), and a road copy's payload is sealed to its recipients rather than
/// carrying each document's own envelope: refused in the first slice (epic #1699, ADR 0961).
/// </summary>
public sealed class OfflineArchiveNotOnThisTenantException : OfflineArchiveException
{
    public OfflineArchiveNotOnThisTenantException()
        : base("OFFLINE_ARCHIVE_NOT_ON_THIS_TENANT", StatusCodes.Status403Forbidden,
            "This organisation seals documents for each reader; offline archives are not offered on it yet.")
    {
    }
}
