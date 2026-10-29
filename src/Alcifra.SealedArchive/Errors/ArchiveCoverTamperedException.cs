namespace Alcifra.SealedArchive.Errors;

/// <summary>
/// The cover's signature does not hold: the cover (name, date, source, recipients, or a hash that binds the payload)
/// was changed after the installation signed it, or the signature is missing. Nothing of the archive is trusted.
/// </summary>
public sealed class ArchiveCoverTamperedException(string reason, Exception? inner = null)
    : SealedArchiveException($"The archive's cover was changed after it was signed ({reason}). It is refused.", inner);
