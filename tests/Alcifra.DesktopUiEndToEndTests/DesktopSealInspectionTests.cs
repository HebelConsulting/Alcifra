namespace Alcifra.UiEndToEndTests;

// The Inspect seal dialog renders (#2291), over a sample report, through the desktop client's own headless hook: a broken
// binding or missing resource throws at construction, which no view-model test sees, and a dialog cannot appear in a
// full-app screenshot. German too, because its strings are the longest and wrap.
public class DesktopSealInspectionTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task The_inspect_seal_dialog_renders_headlessly(string language)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"seal-{Guid.NewGuid():N}.png");

        try
        {
            var (exitCode, output) = await DesktopProc.RunAsync("--seal-screenshot", outPath, "--lang", language);

            Assert.True(exitCode == 0, $"The desktop client exited {exitCode}:\n{output}");
            Assert.False(output.Contains("FAILED", StringComparison.Ordinal), $"The hook reported FAILED:\n{output}");
            Assert.True(File.Exists(outPath), $"No screenshot was produced at {outPath}.\n{output}");
            Assert.True(new FileInfo(outPath).Length > 4096,
                $"The screenshot is {new FileInfo(outPath).Length} bytes — the dialog rendered essentially nothing.");
        }
        finally
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }
}
