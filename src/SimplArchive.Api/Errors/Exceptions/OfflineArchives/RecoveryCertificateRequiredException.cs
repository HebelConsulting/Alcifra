namespace SimplArchive.Api.Errors.Exceptions.OfflineArchives;

/// <summary>
/// The tenant has no recovery certificate for offline exports (ADR 0960, #1695), so no archive is written: one with no
/// recovery path would fail silently years later, when the exporter's card has expired or is lost. The fix is a tenant
/// administrator's: <c>saconsole tenant recovery-certificate set</c>.
/// </summary>
public sealed class RecoveryCertificateRequiredException : OfflineArchiveException
{
    public RecoveryCertificateRequiredException()
        : base("RECOVERY_CERTIFICATE_REQUIRED", StatusCodes.Status409Conflict,
            "This organisation has no recovery certificate for offline exports, so none can be made: an archive nobody can "
            + "recover once the exporter's card expires is not made at all. A tenant administrator sets one with "
            + "'saconsole tenant recovery-certificate set'.")
    {
    }
}
