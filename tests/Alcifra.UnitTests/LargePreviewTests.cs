using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// When a version's preview waits for Open (ADR 1048): over 10 MB as the clients display a size, and never on an unknown size.
public class LargePreviewTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(10L * 1024 * 1024, false)]
    [InlineData(10L * 1024 * 1024 + 1, true)]
    public void Only_a_size_known_to_be_over_the_limit_waits(long? size, bool waits) =>
        Assert.Equal(waits, LargePreview.Waits(size));

    [Theory]
    [InlineData(".mp4", true, true, false)]
    [InlineData("MOV", true, true, false)]
    [InlineData(".mp3", true, false, true)]
    [InlineData(".avi", true, false, false)]
    [InlineData(".pdf", false, false, false)]
    [InlineData("", false, false, false)]
    [InlineData(null, false, false, false)]
    public void A_file_type_says_whether_it_is_media_and_what_a_browser_plays(string? extension, bool media, bool video, bool sound)
    {
        Assert.Equal(media, MediaFiles.IsMedia(extension));
        Assert.Equal(video, MediaFiles.IsVideoABrowserPlays(extension));
        Assert.Equal(sound, MediaFiles.IsSoundABrowserPlays(extension));
    }
}
