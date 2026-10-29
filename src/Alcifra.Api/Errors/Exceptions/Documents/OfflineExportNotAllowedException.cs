namespace Alcifra.Api.Errors.Exceptions.Documents;

/// <summary>
/// The tenant's offline-copy policy (ADR 0923) does not allow offline copies, so no offline export (ADR 0927) is made.
/// The phone reads the same policy and does not offer the copy; reaching this means a stale policy or a hand-built
/// request.
/// </summary>
public sealed class OfflineExportNotAllowedException : DocumentException
{
    public OfflineExportNotAllowedException()
        : base("OFFLINE_EXPORT_NOT_ALLOWED", StatusCodes.Status403Forbidden,
            "This organisation does not allow offline copies.")
    {
    }
}
