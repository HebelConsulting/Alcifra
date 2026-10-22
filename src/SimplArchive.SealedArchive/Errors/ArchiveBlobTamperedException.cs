namespace SimplArchive.SealedArchive.Errors;

/// <summary>
/// A sealed payload file (a document's content, or one of the files that describe the tree) does not match what the
/// signed cover binds: it was changed, swapped or removed. Named by its path in the export layout.
/// </summary>
public sealed class ArchiveBlobTamperedException(string path, Exception? inner = null)
    : SealedArchiveException($"The archive's file '{path}' was changed after it was signed. It is refused.", inner)
{
    /// <summary>The file's path in the export layout (<c>blobs/&lt;sha256&gt;</c>, <c>tree/documents.jsonl</c>, …).</summary>
    public string Path { get; } = path;
}
