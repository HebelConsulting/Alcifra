namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// The tenant refuses plaintext doors and no active Encryption Module allows offline exports on it (ADR 0928). The
/// fix is an administrator's: the module's per-tenant setting, or its licence.
/// </summary>
public sealed class OfflineExportNotAllowedOnThisTenantException : DocumentException
{
    public OfflineExportNotAllowedOnThisTenantException()
        : base("OFFLINE_EXPORT_NOT_ALLOWED_ON_THIS_TENANT", StatusCodes.Status403Forbidden,
            "This organisation seals documents for each reader, and its encryption settings do not allow offline copies.")
    {
    }
}
