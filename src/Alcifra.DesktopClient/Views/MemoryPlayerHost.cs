using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
#if MACOS
using AVKit;
#endif

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The operating system's own player inside the preview (#2036), hosting its native view in the pane. Where this build has
/// no player (<see cref="MediaPlayback.Available"/>) the control is never shown: the preview says the type is not
/// supported, as it did before.
/// </summary>
/// <remarks>
/// <b>It does not start by itself</b> (#2160, owner 2026-11-04): selecting a row shows the video's first frame — its
/// thumbnail, which the ranged reader fetches from the start of the file — with a play button over it, and nothing plays
/// until that button is pressed. The web's preview does the same over its poster, and a person moving through a folder
/// should not have every video they pass begin to play.
/// </remarks>
public sealed class MemoryPlayerHost : Grid
{
    public static readonly StyledProperty<PlayableMedia?> MediaProperty =
        AvaloniaProperty.Register<MemoryPlayerHost, PlayableMedia?>(nameof(Media));

    private readonly PlayerSurface _surface = new();
    private readonly Button _play;

    public MemoryPlayerHost()
    {
        _play = new Button
        {
            Content = "▶",
            // A play control over the first frame (#2160): the glyph is the affordance, the accessible name says what
            // it starts (touch has no hover, as everywhere else).
            FontSize = 34,
            Width = 74,
            Height = 74,
            CornerRadius = new CornerRadius(37),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        AutomationProperties.SetName(_play, Strings.Get("PvPlayVideo"));
        ToolTip.SetTip(_play, Strings.Get("PvPlayVideo"));
        _play.Click += (_, _) =>
        {
            _play.IsVisible = false;
            _surface.Play();
        };
        Children.Add(_surface);
        Children.Add(_play);
    }

    /// <summary>What is shown; null stops and shows nothing.</summary>
    public PlayableMedia? Media
    {
        get => GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty)
        {
            var media = change.GetNewValue<PlayableMedia?>();
            _surface.Show(media);
            _play.IsVisible = media is not null;
        }
    }

    /// <summary>
    /// The native surface itself: a separate control so the play button can sit OVER it — an
    /// <see cref="NativeControlHost"/> cannot host Avalonia content of its own.
    /// </summary>
    private sealed class PlayerSurface : NativeControlHost
    {
#if MACOS
        private AVPlayerView? _view;
        private MemoryPlayer? _player;

        public void Show(PlayableMedia? media)
        {
            Stop();
            if (media is not null && _view is not null)
            {
                _player = new MemoryPlayer(media);
                _view.Player = _player.Player;   // paused: the first frame is the thumbnail (#2160)
            }
        }

        public void Play() => _player?.Player.Play();

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _view = new AVPlayerView { ControlsStyle = AVPlayerViewControlsStyle.Inline, ShowsFullScreenToggleButton = false };
            return new PlatformHandle(_view.Handle, "NSView");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            Stop();
            _view?.Dispose();
            _view = null;
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
#else
        public void Show(PlayableMedia? media)
        {
        }

        public void Play()
        {
        }
#endif
    }
}
