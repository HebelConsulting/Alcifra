namespace Alcifra.UnitTests;

// The repository's tracked tree, `git ls-files`: the same set `git archive` stages and CI checks out. A guard that reads
// the working directory instead also reads what only one machine has (a gitignored backup, an editor's scratch file),
// and fails there and nowhere else (#2024).
//
// A failure to run git throws rather than yielding nothing: these guards exist because the real gates run late, so a
// silently empty local one is worse than none.
internal static class TrackedFiles
{
    // Paths relative to root, with forward slashes, as git prints them.
    public static IReadOnlyList<string> Under(string root)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Could not start git to enumerate the tracked tree.");

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed with exit code {git.ExitCode}.");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
