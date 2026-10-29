namespace Alcifra.Api.Errors.Exceptions.OfflineArchives;

/// <summary>
/// The installation has no signing key for offline archives (ADR 0961): an unsigned archive could not be told from a
/// forged one, so none is made. The fix is the operator's: provision <c>secret/simplarchive/archive-signing</c> in OpenBao
/// (or the <c>OfflineArchive</c> configuration section).
/// </summary>
public sealed class ArchiveSigningKeyMissingException : OfflineArchiveException
{
    public ArchiveSigningKeyMissingException()
        : base("ARCHIVE_SIGNING_KEY_MISSING", StatusCodes.Status503ServiceUnavailable,
            "This installation has no signing key for offline archives yet, so none can be made. The operator provisions one.")
    {
    }
}
