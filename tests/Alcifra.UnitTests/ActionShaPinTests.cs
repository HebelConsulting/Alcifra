using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// Every action a workflow runs is pinned to a COMMIT, never to a tag (#1793).
//
// WHY. A tag is a movable pointer: whoever controls the action's repository can point `v4` at new code, and every
// workflow naming `@v4` runs it on its next trigger, with whatever secrets that job holds. That is how the 2025
// changed-files compromise spread, and a scanner we run (trivy-action) was hit the same way. Several of our jobs
// log in to registries, push packages and sign images, so a moved tag would run inside them.
//
// A 40-hex commit SHA cannot be moved. The trailing `# vX.Y.Z` says which release it is, and Dependabot's
// github-actions updates keep both current, so pinning costs nothing in freshness.
//
// Local actions (`./…`) and container steps (`docker://…`) are not tags on someone else's repository and are
// not this rule's business.
public partial class ActionShaPinTests
{
    [GeneratedRegex(@"^\s*-?\s*uses:\s*([^\s#]+)", RegexOptions.Multiline)]
    private static partial Regex Uses();

    [GeneratedRegex(@"@[0-9a-f]{40}$")]
    private static partial Regex FullSha();

    [Fact]
    public void Every_workflow_action_is_pinned_to_a_commit_sha()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var workflows = Path.Combine(root, ".github", "workflows");
        Assert.True(Directory.Exists(workflows), $"{workflows} is missing — this guard has nothing to read.");

        var files = Directory.EnumerateFiles(workflows, "*.yml").Concat(Directory.EnumerateFiles(workflows, "*.yaml")).ToList();
        var uses = files
            .SelectMany(f => Uses().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Ref: m.Groups[1].Value)))
            .ToList();

        // A sentinel: a workflow shape this regex stopped reading would otherwise pass with nothing checked.
        Assert.True(uses.Count >= 10, $"Only {uses.Count} `uses:` lines matched in {files.Count} workflows — the shape changed and this guard stopped seeing them.");

        var floating = uses
            .Where(u => !u.Ref.StartsWith("./", StringComparison.Ordinal) && !u.Ref.StartsWith("docker://", StringComparison.Ordinal))
            .Where(u => !FullSha().IsMatch(u.Ref))
            .Select(u => $"  {u.File}: {u.Ref}")
            .Distinct()
            .ToList();

        Assert.True(floating.Count == 0,
            $"These actions are pinned to a movable tag, not a commit:\n{string.Join("\n", floating)}\n\n"
            + "Pin each to the full 40-hex commit SHA of its release, with the release as a comment "
            + "(`owner/action@<sha> # v1.2.3`); Dependabot keeps both current (#1793).");
    }
}
