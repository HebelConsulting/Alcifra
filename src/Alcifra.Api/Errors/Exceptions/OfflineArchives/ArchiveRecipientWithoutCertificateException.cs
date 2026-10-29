namespace Alcifra.Api.Errors.Exceptions.OfflineArchives;

/// <summary>
/// A person the archive is for has no usable certificate (ADR 0842's set, or their own column without the Encryption
/// Module), so the archive could not be opened by them: refused rather than silently leaving them out.
/// </summary>
public sealed class ArchiveRecipientWithoutCertificateException : OfflineArchiveException
{
    public ArchiveRecipientWithoutCertificateException(string who)
        : base("ARCHIVE_RECIPIENT_WITHOUT_CERTIFICATE", StatusCodes.Status409Conflict,
            $"{who} has no certificate an archive can be sealed to. They enrol one, or the archive is made without them.")
    {
    }
}
