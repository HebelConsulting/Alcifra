namespace SimplArchive.SealedArchive.Errors;

/// <summary>
/// The cover is signed, but by a key none of the trusted installations ever published: the archive did not come from
/// an installation this reader knows, whatever its cover claims.
/// </summary>
public sealed class ArchiveForeignSignatureException(string thumbprint)
    : SealedArchiveException($"The archive is signed by a key no known installation published (thumbprint {thumbprint}). It is refused.")
{
    /// <summary>The SHA-256 thumbprint of the certificate that signed it.</summary>
    public string Thumbprint { get; } = thumbprint;
}
