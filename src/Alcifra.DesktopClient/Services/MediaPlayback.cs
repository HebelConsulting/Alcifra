using Alcifra.ApiClient;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.Services;

/// <summary>A video or a sound as the player is handed it: where its bytes are read from, and the uniform type they are.</summary>
/// <param name="Bytes">Held in memory, or read from storage a range at a time (<see cref="IMediaBytes"/>).</param>
/// <param name="Identifier">Its uniform type identifier (<see cref="MediaTypes.IdentifierOf"/>): the player is handed bytes, not a
/// file, so it reads the container from what it is told rather than from a name.</param>
public sealed record PlayableMedia(IMediaBytes Bytes, string Identifier);

/// <summary>
/// Whether this build of the desktop plays video and sound from memory with the operating system's own player (#2036),
/// and what it plays. The player exists where the build targets the Apple frameworks (<c>net10.0-macos</c>): there the
/// system's player reads the bytes through a resource loader, as the phone's does (Mobile ADR 0030), and nothing is
/// written to storage. Every other build keeps ADR 0967's rule: a video or a sound goes behind <em>Open with…</em>.
/// Which types play is the Messenger's rule, <see cref="MediaTypes"/>, so a sent file and a document play the same way.
/// </summary>
public static class MediaPlayback
{
    /// <summary>The player is part of this build and of this operating system.</summary>
    public static bool Available { get; internal set; } =
#if MACOS
        OperatingSystem.IsMacOS();
#else
        false;
#endif

    /// <summary>
    /// What <paramref name="contentType"/> plays as, or null where this build does not play it: not a media type, or no
    /// player in this build.
    /// </summary>
    public static PlayableMedia? Of(byte[] content, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Available)
        {
            return null;
        }

        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return MediaTypes.IdentifierOf(type) is { } identifier ? new PlayableMedia(new MemoryMediaBytes(content), identifier) : null;
    }

    /// <summary>The content type a version's extension names, for storage that serves every version as octet-stream.</summary>
    public static string TypeOf(string fileExtension) => MimeKit.MimeTypes.GetMimeType("media" + fileExtension);

    /// <summary>
    /// A document's video or sound read from storage a range at a time (owner, 2026-11-11), or null where it is not: no
    /// player, not a type that plays, or an address that is not ranged storage (<see cref="RangedMediaBytes.OpenAsync"/>).
    /// A null sends the caller to read the version whole, as a sealed one must be.
    /// </summary>
    public static async Task<PlayableMedia?> StreamedAsync(Preview preview, CancellationToken cancellationToken = default)
    {
        if (!Available || preview.DownloadUrl is not { } url
            || MediaTypes.IdentifierOf(TypeOf(preview.FileExtension)) is not { } identifier)
        {
            return null;
        }

        return await RangedMediaBytes.OpenAsync(url, cancellationToken) is { } ranged ? new PlayableMedia(ranged, identifier) : null;
    }
}
