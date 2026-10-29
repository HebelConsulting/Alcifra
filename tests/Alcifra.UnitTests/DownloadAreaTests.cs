using Alcifra.Api.Download;

namespace Alcifra.UnitTests;

// Which requests the download area answers with this server's manual (ADR 0997).
public class DownloadAreaTests
{
    private const string Manual = "/download/manual/Edition-Manual.pdf";

    [Theory]
    [InlineData("/download/manual/Alcifra-Manual.pdf", true)]
    [InlineData("/download/manual/Another-Edition-Manual.pdf", true)]
    [InlineData(Manual, false)]
    [InlineData("/download/manual/sub/Other-Manual.pdf", false)]
    [InlineData("/download/clients/Other-Manual.pdf", false)]
    [InlineData("/download/manual/index.html", false)]
    public void Another_manual_name_is_recognised(string path, bool redirects) =>
        Assert.Equal(redirects, DownloadArea.IsAnotherManualName(path, Manual));
}
