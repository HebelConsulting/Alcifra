using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// A workflow decides where it runs by the repository's ROLE, the REPO_ROLE variable, never by its name (ADR 1018).
// A name comparison turns false the moment the repository is renamed, and GitHub reports the job as skipped, not
// failed: publishing and the vulnerability scan would simply stop, and an inverted guard would start running the
// public mirror's job here. Who publishes a release stays the brand's to say (scripts/brand-release-role.sh).
public class WorkflowRoleGateTests
{
    private static readonly Regex NameComparison =
        new(@"github\.repository\s*[!=]=\s*'[^']+'", RegexOptions.Compiled);

    [Fact]
    public void No_workflow_compares_the_repository_name()
    {
        var root = RepoPaths.Root();
        var offenders = TrackedFiles.Under(root)
            .Where(path => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal)))
            .SelectMany(path => File.ReadLines(Path.Combine(root, path))
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => NameComparison.IsMatch(entry.line))
            .Select(entry => $"{entry.path}:{entry.number}: {entry.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Gate on `vars.REPO_ROLE` ('private' or 'public'), not on the repository name (ADR 1018):\n{string.Join('\n', offenders)}");
    }

    // The guard reads only what the mirror has too (.github/workflows is published), so it needs no private gate.
    [Fact]
    public void The_roles_in_use_are_the_two_documented_ones()
    {
        var root = RepoPaths.Root();
        var roles = TrackedFiles.Under(root)
            .Where(path => path.StartsWith(".github/workflows/", StringComparison.Ordinal))
            .SelectMany(path => Regex.Matches(File.ReadAllText(Path.Combine(root, path)), @"vars\.REPO_ROLE\s*[!=]=\s*'([^']*)'"))
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

        Assert.NotEmpty(roles);
        Assert.Subset(new HashSet<string> { "private", "public" }, roles);
    }
}
