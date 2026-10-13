using System.IO.Compression;

namespace SimplArchive.ApiClient.Offline;

/// <summary>
/// Where an archive in the export layout (core ADR 0347) is read from: a zip (an exported <c>.simplarchive</c>) or a
/// directory (the phone's offline cache), behind one interface so the responder reads both the same way (owner
/// decision 2026-10-13, #1697). Paths are the layout's own, with forward slashes: <c>manifest.json</c>,
/// <c>tree/documents.jsonl</c>, <c>blobs/&lt;sha256&gt;</c>.
/// </summary>
public interface IArchiveStorage
{
    /// <summary>The entry's bytes, or null when the archive has no such entry.</summary>
    byte[]? Read(string path);
}

/// <summary>An archive read from a zip (an exported <c>.simplarchive</c>, or the server's export download).</summary>
public sealed class ZipArchiveStorage(byte[] zip) : IArchiveStorage
{
    public byte[]? Read(string path)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.GetEntry(path) is not { } entry)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// An archive read from a directory, each file passed through <paramref name="open"/> on the way out: the phone's
/// cache keeps every file encrypted on its own (SimplArchiveMobile #5), and decrypts here. Null for plain files.
/// </summary>
public sealed class DirectoryArchiveStorage(string directory, Func<string, byte[], byte[]>? open = null) : IArchiveStorage
{
    public byte[]? Read(string path)
    {
        var full = Path.GetFullPath(Path.Combine(directory, path));
        // An entry path never leaves the archive's directory, whatever a link says.
        if (!full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(full);
        return open is null ? bytes : open(path, bytes);
    }
}
