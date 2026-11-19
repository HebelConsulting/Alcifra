using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// A video or a sound in the preview (#2036): where this build has the operating system's player, the bytes go to it from
// memory and nothing else is shown; where it has none, the type stays unsupported, as ADR 0967 left it. The player itself
// is a native view no headless test can drive (Program's --play-check proves it on a Mac); what is tested here is the one
// switch in the view model that decides between the two, and that a reset stops the player (its media goes).
public class DesktopMediaPreviewTests
{
    // The first bytes of an MP4 ("ftyp" at offset 4): enough for the kind sniffers to see no PDF, image or text.
    private static readonly byte[] Mp4 = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];

    [Fact]
    public async Task Without_a_player_a_video_stays_unsupported()
    {
        var was = MediaPlayback.Available;
        MediaPlayback.Available = false;
        try
        {
            var vm = new PreviewViewModel(new TestShell());
            Assert.False(await vm.ShowContentAsync(Mp4, "video/mp4"));
            Assert.Null(vm.Media);
            Assert.False(vm.HasPreview);
            Assert.Contains("not supported", vm.PreviewPlaceholder);
        }
        finally
        {
            MediaPlayback.Available = was;
        }
    }

    [Fact]
    public async Task With_a_player_a_video_is_handed_to_it_from_memory_and_a_reset_takes_it_away()
    {
        var was = MediaPlayback.Available;
        MediaPlayback.Available = true;
        try
        {
            var vm = new PreviewViewModel(new TestShell());
            Assert.True(await vm.ShowContentAsync(Mp4, "video/mp4; codecs=avc1"));
            Assert.NotNull(vm.Media);
            Assert.Equal(Mp4, await vm.Media!.Bytes.ReadAsync(0, Mp4.Length, CancellationToken.None));
            Assert.Equal("public.mpeg-4", vm.Media.Identifier);
            Assert.True(vm.HasPreview);
            Assert.Null(vm.PreviewPlaceholder);
            Assert.True(vm.ContentWasEnveloped, "content shown from memory is what the card's removal discards");

            vm.Reset("Select a document.");
            Assert.Null(vm.Media);
            Assert.False(vm.HasPreview);
        }
        finally
        {
            MediaPlayback.Available = was;
        }
    }

    [Fact]
    public async Task A_type_the_phone_does_not_play_is_not_played_here_either()
    {
        var was = MediaPlayback.Available;
        MediaPlayback.Available = true;
        try
        {
            var vm = new PreviewViewModel(new TestShell());
            Assert.False(await vm.ShowContentAsync([0x1A, 0x45, 0xDF, 0xA3], "video/webm"));
            Assert.Null(vm.Media);
        }
        finally
        {
            MediaPlayback.Available = was;
        }
    }
}

// A document's video is read from storage a range at a time, as the player asks (owner, 2026-11-11): the preview hands the
// player a ranged source over the version's presigned address, so playback starts without the whole file. Against the
// real stack, whose object storage answers ranges as S3 does.
[Collection(UiCollection.Name)]
public class DesktopMediaStreamingTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_documents_video_is_read_by_ranges_from_storage_not_whole()
    {
        DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(app.BaseUrl));
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var video = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(video);
        "\0\0\0\x18ftypisom"u8.CopyTo(video);
        var id = await api.Documents.UploadFileAsync(repo.Href("children"), $"clip-{Guid.NewGuid():N}.mp4", video);
        var row = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Id == id);
        var preview = await api.Documents.GetPreviewAsync(row.Href("versions"));

        var ranged = await RangedMediaBytes.OpenAsync(preview.DownloadUrl!);
        Assert.NotNull(ranged);
        Assert.Equal(video.LongLength, ranged!.Length);
        Assert.Equal(video.AsSpan(2 * 1024 * 1024, 4096).ToArray(), await ranged.ReadAsync(2 * 1024 * 1024, 4096, CancellationToken.None));

        var was = MediaPlayback.Available;
        MediaPlayback.Available = true;
        try
        {
            var vm = new PreviewViewModel(new TestShell()) { Api = api };
            await vm.RenderAsync(preview);
            Assert.IsType<RangedMediaBytes>(vm.Media!.Bytes);
            Assert.Equal("public.mpeg-4", vm.Media.Identifier);
        }
        finally
        {
            MediaPlayback.Available = was;
        }
    }
}
