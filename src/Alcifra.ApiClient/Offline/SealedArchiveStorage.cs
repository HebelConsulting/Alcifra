using Alcifra.SealedArchive;

namespace Alcifra.ApiClient.Offline;

/// <summary>
/// An opened <c>.simplarchive</c> (ADR 0961) as the responder's storage: its payload is the export layout of ADR 0347,
/// so <see cref="OfflineArchive"/> and <see cref="OfflineResponder"/> read it as they read an export, and every file is
/// checked against the signed cover before it is handed out.
/// </summary>
/// <remarks>The desktop's reader (#1698) opens the archive with the card through
/// <see cref="SealedArchiveReader.OpenAsync"/> and hands the result here; the cover alone (the banner, the server list)
/// needs no key, through <see cref="SealedArchiveReader.ReadCover"/>.</remarks>
public sealed class SealedArchiveStorage(OpenedSealedArchive archive) : IArchiveStorage
{
    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(archive.Read(path));
}
