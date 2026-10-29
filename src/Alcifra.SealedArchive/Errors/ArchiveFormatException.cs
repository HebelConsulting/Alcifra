namespace Alcifra.SealedArchive.Errors;

/// <summary>The file is not a <c>.simplarchive</c> this reader understands: not a zip, a part missing, or a newer format.</summary>
public sealed class ArchiveFormatException(string reason, Exception? inner = null)
    : SealedArchiveException($"This is not an archive this version can read: {reason}.", inner);
