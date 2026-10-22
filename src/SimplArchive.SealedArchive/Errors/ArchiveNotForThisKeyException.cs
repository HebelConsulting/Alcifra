namespace SimplArchive.SealedArchive.Errors;

/// <summary>The key offered cannot open the archive's payload key: it was not sealed to this certificate.</summary>
public sealed class ArchiveNotForThisKeyException(Exception? inner = null)
    : SealedArchiveException("This key cannot open the archive: it was not sealed to it. The cover names the certificates that can.", inner);
