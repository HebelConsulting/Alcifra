namespace Alcifra.UiEndToEndTests;

// The folder picker opened for real, with nobody loading it first (#2146): Move, the bulk bar's Move, Place reference and
// the certificate folder each opened it that way and showed no folders.
[Collection(UiCollection.Name)]
public class DesktopFolderPickerTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFolderPickerTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_picker_fills_itself_when_it_opens()
    {
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);

        var (exitCode, output) = await DesktopProc.RunAsync("--folder-picker-test", _app.BaseUrl, token);

        Assert.True(exitCode == 0, $"The hook exited {exitCode}. Output:\n{output}");
        Assert.Contains("The picker opened with", output, StringComparison.Ordinal);
        Assert.Contains("comment box(es) showing", output, StringComparison.Ordinal);
        Assert.False(output.Contains("FAILED", StringComparison.Ordinal), $"The hook reported FAILED. Output:\n{output}");
    }
}
