namespace Alcifra.UiEndToEndTests;

// The Devices window renders (#2394): its per-row Connect WebDAV column and the open Connect panel, through the
// desktop client's own headless hook. A broken binding or missing resource throws at load, which no view-model test
// sees, and a Window cannot appear in a full-app screenshot. German too — its buttons are the longest and wrap.
public class DesktopDevicesWindowTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task The_devices_window_renders_headlessly(string language)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"devices-{Guid.NewGuid():N}.png");

        try
        {
            var (exitCode, output) = await DesktopProc.RunAsync("--devices-screenshot", outPath, "--rows", "--lang", language);

            Assert.True(exitCode == 0, $"The desktop client exited {exitCode}:\n{output}");
            Assert.False(output.Contains("FAILED", StringComparison.Ordinal), $"The hook reported FAILED:\n{output}");
            Assert.True(File.Exists(outPath), $"No screenshot was produced at {outPath}.\n{output}");
            Assert.True(new FileInfo(outPath).Length > 4096,
                $"The screenshot is {new FileInfo(outPath).Length} bytes — the window rendered essentially nothing.");
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
