using System.Text;
using System.Text.Json;

namespace Alcifra.ApiClient.Offline;

/// <summary>
/// An offline export whose index data and chat travel sealed (core ADR 0928): <c>tree/sealed.p7m</c>, one S/MIME
/// artefact addressed to the caller's certificates, holding <c>tree/index-data.jsonl</c> and <c>tree/chat.jsonl</c>.
/// </summary>
/// <remarks>
/// The host opens the sealed parts with the user's key. A device opens every sealed part of one update in ONE key
/// dialog (owner decision 2026-10-14: one PIN for an entire cache update), so the parts are handed out first and put
/// back opened; <see cref="ArchiveMerger"/> then merges an opened export as any other.
/// </remarks>
public static class SealedExport
{
    public const string Path = "tree/sealed.p7m";

    /// <summary>The export's sealed part, or null when its index data and chat travel as they are.</summary>
    public static Task<byte[]?> SealedPartAsync(IArchiveStorage export, CancellationToken cancellationToken = default) =>
        export.ReadAsync(Path, cancellationToken);

    /// <summary>The export with its sealed part opened: the two files read as if they had travelled plain.</summary>
    /// <param name="opened">What the user's key returned for <see cref="SealedPartAsync"/> (the S/MIME payload).</param>
    public static IArchiveStorage WithOpened(IArchiveStorage export, byte[] opened) =>
        new Opened(export, JsonSerializer.Deserialize<Dictionary<string, string>>(opened)
            ?? throw new OfflineArchiveException("The sealed part of the export holds nothing."));

    private sealed class Opened(IArchiveStorage export, IReadOnlyDictionary<string, string> files) : IArchiveStorage
    {
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            files.TryGetValue(path, out var text)
                ? Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(text))
                : path == SealedExport.Path ? Task.FromResult<byte[]?>(null) : export.ReadAsync(path, cancellationToken);
    }
}
