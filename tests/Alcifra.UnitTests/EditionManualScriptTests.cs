using System.Diagnostics;

namespace Alcifra.UnitTests;

/// <summary>
/// <c>scripts/edition-manual.sh</c> as the release runs it (desktop-packages.yml): no CI job ran this path, so an exit 1
/// on a brand without palette variants reached the v0.62.2 tag and failed the step that attaches the packages.
/// </summary>
public sealed class EditionManualScriptTests
{
    [Fact]
    public void Without_palette_variants_it_succeeds_and_prints_only_the_manual()
    {
        var start = new ProcessStartInfo("bash", ["--noprofile", "--norc", "-eo", "pipefail", Path.Combine("scripts", "edition-manual.sh")])
        {
            WorkingDirectory = RepoPaths.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var bash = Process.Start(start)!;
        var output = bash.StandardOutput.ReadToEnd();
        var error = bash.StandardError.ReadToEnd();
        bash.WaitForExit();

        Assert.True(bash.ExitCode == 0, $"exit {bash.ExitCode}: {error}");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([$"src/Alcifra.Api/wwwroot/download/manual/{Alcifra.Theming.Brand.Shipped.ManualFileName}"], lines);
    }
}
