namespace Alcifra.UnitTests;

// The Typst install runs on Linux and, since #2166, on the release's Mac runner, whose tar is not GNU tar. `--wildcards` is
// GNU-only: on the Mac tar refused it and exited, curl kept retrying into the closed pipe, and the step hung until the job's
// sixty-minute limit, twice, so v0.59.0 got no desktop packages. Nothing runs this path before a tag, so the rule is
// guarded here: no GNU-only tar flag, and no download piped into tar, which is what turned a refusal into a hang.
public class SetupTypstActionTests
{
    private static string Action() =>
        File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "actions", "setup-typst", "action.yml"));

    [Fact]
    public void The_install_uses_no_tar_flag_the_macs_tar_refuses() =>
        Assert.DoesNotContain("--wildcards", Action(), StringComparison.Ordinal);

    [Fact]
    public void The_download_is_not_piped_into_tar_and_is_bounded_in_time()
    {
        var action = Action();
        Assert.DoesNotMatch(@"\|\s*tar\b", action);
        Assert.Contains("--max-time", action, StringComparison.Ordinal);
    }
}
