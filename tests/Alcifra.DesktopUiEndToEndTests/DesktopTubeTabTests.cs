using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.UiEndToEndTests;

// The desktop's Tube tab (#2148): the channels with their lock, the rows and their marks, the filter, the reason for an
// empty list, and actions that follow the selected row.
public class DesktopTubeTabTests
{
    private static TubeVideoRow Video(string name, bool published = true, bool canEdit = false, bool canRemove = false, string? watch = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Published = published,
        CanEdit = canEdit,
        CanRemoveFromChannel = canRemove,
        WatchUrl = watch,
    };

    private static TubeChannelRow Channel(string name, bool canWrite, params TubeVideoRow[] videos) =>
        new() { Id = Guid.NewGuid(), Name = name, CanWrite = canWrite, Videos = [.. videos] };

    private static TubeTabViewModel Tab(params TubeChannelRow[] channels)
    {
        var tab = new TubeTabViewModel(new TestShell(), () => null);
        tab.Adopt(new TubeShelfAnswer { Items = [.. channels] });
        return tab;
    }

    [Fact]
    public void Every_channel_starts_ticked_and_one_the_person_cannot_write_to_is_locked()
    {
        var tab = Tab(Channel("Open", true, Video("A")), Channel("Closed", false, Video("B")));

        Assert.All(tab.Channels, c => Assert.True(c.IsChecked));
        Assert.Equal([false, true], tab.Channels.Select(c => c.Locked));
        Assert.Equal(2, tab.Rows.Count);
    }

    [Fact]
    public void Unticking_and_filtering_narrow_the_list_and_a_reload_keeps_the_ticks()
    {
        var open = Channel("Open", true, Video("Keynote"), Video("Demo"));
        var closed = Channel("Closed", false, Video("Other"));
        var tab = Tab(open, closed);

        tab.Filter = "key";
        Assert.Equal("Keynote", Assert.Single(tab.Rows).Video.Name);

        tab.Filter = string.Empty;
        tab.Channels[1].IsChecked = false;
        Assert.Equal(2, tab.Rows.Count);

        tab.Adopt(new TubeShelfAnswer { Items = [open, closed] });
        Assert.False(tab.Channels[1].IsChecked);
        Assert.Equal(2, tab.Rows.Count);
    }

    [Fact]
    public void An_empty_list_says_why()
    {
        var tab = new TubeTabViewModel(new TestShell(), () => null);
        tab.Adopt(new TubeShelfAnswer { EncryptsAtRest = true });
        Assert.Equal(Strings.Get("TubeEncrypts"), tab.EmptyMessage);

        tab.Adopt(new TubeShelfAnswer());
        Assert.Equal(Strings.Get("TubeNoChannels"), tab.EmptyMessage);

        tab = Tab(Channel("Open", true));
        Assert.Equal(Strings.Get("TubeEmpty"), tab.EmptyMessage);
        tab.Channels[0].IsChecked = false;
        Assert.Equal(Strings.Get("TubeNoneSelected"), tab.EmptyMessage);
    }

    [Fact]
    public void The_actions_follow_the_selected_row_and_publish_waits_for_its_own_read()
    {
        var watched = Video("Watched", watch: "https://video.example.test/videos/watch/1");
        var reference = Video("Reference", canRemove: true);
        var editable = Video("Draft", published: false, canEdit: true);
        var tab = Tab(Channel("Open", true, watched, reference, editable));

        tab.Selected = tab.Rows.Single(r => r.Video == watched);
        Assert.True(tab.CanWatch);
        Assert.False(tab.CanRemove);
        Assert.False(tab.CanPublish);
        Assert.Null(tab.WatchHint);

        tab.Selected = tab.Rows.Single(r => r.Video == reference);
        Assert.False(tab.CanWatch);
        Assert.True(tab.CanRemove);
        Assert.NotNull(tab.WatchHint);

        // No read has filled the pane here, so nothing may be sent yet (ADR 0794).
        tab.Selected = tab.Rows.Single(r => r.Video == editable);
        Assert.False(tab.CanPublish);
        Assert.Equal(Strings.Get("TubePublish"), tab.PublishLabel);
    }

    [Fact]
    public void A_video_is_offered_only_the_channels_that_can_still_take_it()
    {
        var video = Video("Clip");
        var tab = Tab(Channel("Holds it", true, video), Channel("Free", true), Channel("Locked", false));

        tab.Selected = tab.Rows.Single();

        Assert.Equal("Free", Assert.Single(tab.PlaceTargets).Name);
        Assert.True(tab.CanPlace);
    }

    // The add sits on the channel row it files into, and only where the person may create there (ADR 0559); without a
    // session nothing is sent and nothing is reported.
    [Fact]
    public async Task Only_a_channel_the_person_can_write_to_offers_an_add()
    {
        var shell = new TestShell();
        var tab = new TubeTabViewModel(shell, () => null);
        tab.Adopt(new TubeShelfAnswer { Items = [Channel("Open", true), Channel("Closed", false)] });

        Assert.Equal([true, false], tab.Channels.Select(c => c.CanAdd));

        await tab.AddAsync(tab.Channels[0].Row, [("clip.mp4", [1, 2, 3])]);
        Assert.Empty(shell.Reports);
    }

    [Fact]
    public void Signing_out_forgets_the_channels()
    {
        var tab = Tab(Channel("Open", true, Video("A")));

        tab.Reset();

        Assert.False(tab.IsOffered);
        Assert.Empty(tab.Channels);
        Assert.Empty(tab.Rows);
    }
}
