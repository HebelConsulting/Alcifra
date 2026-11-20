#if MACOS
using System.Globalization;
using Avalonia;
using AVFoundation;
using CoreMedia;
using Foundation;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Plays a file from memory with no window (#2036, <c>--play-check</c>): the one proof a headless run can give that the
/// loader feeds the system's player and that it decodes, seeks and advances. The player is the production one; only the
/// view is missing. Reports through the log (ADR 0906); true when the position moved while playing and a seek landed.
/// </summary>
internal static class MemoryPlayerCheck
{
    /// <summary>
    /// <c>--play-check &lt;file&gt;</c>: plays it with no window and exits 0 when it played and seeked.
    /// <c>--play-window &lt;file&gt;</c>: the production view (<see cref="Views.MemoryPlayerHost"/>) in a real window, for a
    /// person to watch; returns when the window closes. False when neither flag is given.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        var check = Array.IndexOf(args, "--play-check");
        if (check >= 0 && check + 1 < args.Length)
        {
            Environment.Exit(Run(args[check + 1]) ? 0 : 1);
        }

        var window = Array.IndexOf(args, "--play-window");
        if (window < 0 || window + 1 >= args.Length)
        {
            return false;
        }

        var path = args[window + 1];
        var content = File.ReadAllBytes(path);
        var opening = FileOpening.Of(content, Path.GetFileName(path), "application/octet-stream", filesStayInApp: false);
        if (MediaPlayback.Of(content, opening.ContentType) is not { } media)
        {
            DesktopLog.Warn("play-window: {Path} is {ContentType}, which this build does not play.", path, opening.ContentType);
            return true;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithBrandFont().SetupWithoutStarting();
        var shown = new Avalonia.Controls.Window
        {
            Title = $"#2036 player check: {Path.GetFileName(path)}",
            Width = 960,
            Height = 600,
            Content = new Views.MemoryPlayerHost { Media = media },
        };
        // The UI thread's loop until the window closes: no lifetime, so the app's own start (the logon window) never runs.
        using var closed = new CancellationTokenSource();
        shown.Closed += (_, _) => closed.Cancel();
        shown.Show();
        Avalonia.Threading.Dispatcher.UIThread.MainLoop(closed.Token);
        return true;
    }

    public static bool Run(string path)
    {
        var content = File.ReadAllBytes(path);
        var opening = FileOpening.Of(content, Path.GetFileName(path), "application/octet-stream", filesStayInApp: false);
        if (MediaPlayback.Of(content, opening.ContentType) is not { } media)
        {
            DesktopLog.Warn("play-check: {Path} is {ContentType}, which this build does not play.", path, opening.ContentType);
            return false;
        }

        DesktopLog.Info("play-check: {Bytes} bytes of {ContentType} as {Identifier}", content.Length, opening.ContentType, media.Identifier);
        using var player = new MemoryPlayer(media);
        player.Player.Play();

        // Wait (bounded) until the item is READY before measuring anything: on a loaded machine the first load stayed
        // Unknown for 15 s, and a check that measured "playing" straight away saw a still position and said no
        // (2026-11-19). The wait is the player's own work, not a budget for the test.
        var loadingFor = DateTime.UtcNow;
        while (player.Player.CurrentItem?.Status is not AVPlayerItemStatus.ReadyToPlay
            && DateTime.UtcNow - loadingFor < TimeSpan.FromSeconds(30))
        {
            NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.25));
        }

        // Advanced means the position MOVED, not that it reached a particular second: a check that compared the last
        // position to 2 s failed a 1.5 s clip that had played from end to end (found on the packaged app, 2026-11-19).
        var (first, lastPlaying) = Watch(player.Player, "playing", TimeSpan.FromSeconds(5));
        var advanced = lastPlaying > first + 0.2;

        var target = Math.Min(5, Math.Max(1, player.Player.CurrentItem?.Duration.Seconds / 2 ?? 1));
        player.Player.Seek(CMTime.FromSeconds(target, 600));
        var (_, lastSeeked) = Watch(player.Player, "after a seek to " + target.ToString("0.0", CultureInfo.InvariantCulture), TimeSpan.FromSeconds(2));
        var landed = lastSeeked >= target - 0.5;

        var error = player.Player.CurrentItem?.Error?.LocalizedDescription ?? player.Player.Error?.LocalizedDescription;
        if (error is not null)
        {
            DesktopLog.Warn("play-check: the player reports {Error}", error);
        }

        if (!advanced && error is null && player.Player.CurrentItem?.Status is AVPlayerItemStatus.ReadyToPlay)
        {
            // Seen on 2026-11-19: the item was ready and the rate 1, yet the clock stood still — and `afplay` on the
            // system's own sound hung too, so this machine's audio output was not rendering (a wedged coreaudiod).
            // The app was fine; the check says which half failed.
            DesktopLog.Warn("play-check: the item is ready and playing but the position did not move — the system's audio output may not be rendering (if `afplay /System/Library/Sounds/Ping.aiff` hangs too, it is CoreAudio, not the app).");
        }

        DesktopLog.Info("play-check: advanced {Advanced}, seek landed {Landed}, status {Status}", advanced, landed, player.Player.CurrentItem?.Status);
        return advanced && landed && error is null;
    }

    // Polls the position for a while and logs it; the first and last positions seen, in seconds — the first alone
    // tells whether it ever moved. The main thread's run loop is pumped while waiting: the player's loading and status
    // changes are delivered through it, so a thread that merely sleeps never sees the item leave Unknown.
    private static (double First, double Last) Watch(AVPlayer player, string phase, TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        var first = double.NaN;
        var last = 0.0;
        while (DateTime.UtcNow < deadline)
        {
            NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.5));
            last = player.CurrentTime.Seconds;
            if (double.IsNaN(first))
            {
                first = last;
            }

            DesktopLog.Info("play-check: {Phase}: position {Seconds} s, rate {Rate}, item {Status}", phase,
                last.ToString("0.00", CultureInfo.InvariantCulture), player.Rate, player.CurrentItem?.Status);
        }

        return (double.IsNaN(first) ? 0.0 : first, last);
    }
}
#endif
