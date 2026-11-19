using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Alcifra.DesktopClient.Services;
#if MACOS
using AVKit;
#endif

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The operating system's own player inside the preview (#2036), hosting its native view in the pane and playing
/// <see cref="Media"/> from memory. Where this build has no player (<see cref="MediaPlayback.Available"/>) the control is
/// never shown: the preview says the type is not supported, as it did before.
/// </summary>
public sealed class MemoryPlayerHost : NativeControlHost
{
    public static readonly StyledProperty<PlayableMedia?> MediaProperty = AvaloniaProperty.Register<MemoryPlayerHost, PlayableMedia?>(nameof(Media));

    /// <summary>What plays; null stops and shows nothing.</summary>
    public PlayableMedia? Media
    {
        get => GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

#if MACOS
    private AVPlayerView? _view;
    private MemoryPlayer? _player;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _view = new AVPlayerView { ControlsStyle = AVPlayerViewControlsStyle.Inline, ShowsFullScreenToggleButton = false };
        Play(Media);
        return new PlatformHandle(_view.Handle, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Stop();
        _view?.Dispose();
        _view = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty && _view is not null)
        {
            Stop();
            Play(change.GetNewValue<PlayableMedia?>());
        }
    }

    private void Play(PlayableMedia? media)
    {
        if (media is null || _view is null)
        {
            return;
        }

        _player = new MemoryPlayer(media);
        _view.Player = _player.Player;
        _player.Player.Play();
    }

    private void Stop()
    {
        if (_view is not null)
        {
            _view.Player = null;
        }

        _player?.Dispose();
        _player = null;
    }
#endif
}
