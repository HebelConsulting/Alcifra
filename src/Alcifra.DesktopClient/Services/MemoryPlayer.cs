#if MACOS
using AVFoundation;
using CoreFoundation;
using Foundation;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The system's player over bytes in memory (#2036): an asset at an address of a scheme of our own, which the system
/// cannot fetch, so it asks <see cref="MemoryAssetLoader"/> for every byte instead. Nothing is written to storage. One
/// per thing played; <see cref="Dispose"/> stops it and lets the system's player go.
/// </summary>
internal sealed class MemoryPlayer : IDisposable
{
    private readonly MemoryAssetLoader _loader;

    public MemoryPlayer(PlayableMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _loader = new MemoryAssetLoader(media.Bytes, media.Identifier);
        var asset = new AVUrlAsset(MemoryAssetLoader.Address);
        asset.ResourceLoader.SetDelegate(_loader, new DispatchQueue("Alcifra.Desktop.MemoryPlayer"));
        Player = new AVPlayer(new AVPlayerItem(asset));
    }

    /// <summary>The system's player, for a view to show and for the controls to drive.</summary>
    public AVPlayer Player { get; }

    public void Dispose()
    {
        Player.Pause();
        Player.ReplaceCurrentItemWithPlayerItem(null);
        Player.Dispose();
        _loader.Dispose();
    }
}
#endif
