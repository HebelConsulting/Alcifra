using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// No build asks a distribution's package mirror (#2325). On 2026-11-11 Ubuntu's mirror first timed out, then stopped
// resolving from GitHub's runners for hours, and every PR that built the image or the manual went red, a two-line pin
// fix the kiosk's nightly reset depended on among them. What a build needs is part of the repository, in our own
// registry pinned by digest, or a pinned download with its checksum.
public partial class NoDistributionMirrorTests
{
    // The places that still ask one, each with what removes it. This list may only get shorter, and an entry that no
    // longer matches anything fails too, so a removal cannot leave its exception behind.
    private static readonly Dictionary<string, string> NotYetRemoved = new(StringComparer.Ordinal)
    {
        ["Dockerfile"] = "The final stage's `apk upgrade` and `apk add icu-libs curl krb5-libs tzdata`: FROM base/runtime, pinned by digest once its first build exists (#2325, step 4b).",
    };

    [Fact]
    public void No_dockerfile_or_workflow_installs_from_a_distribution_mirror()
    {
        var root = RepoPaths.Root();
        var asking = Candidates(root)
            .Where(path => Install().IsMatch(File.ReadAllText(Path.Combine(root, path))))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(asking.SetEquals(NotYetRemoved.Keys),
            $"Ask a distribution mirror: {string.Join(", ", asking.Order())}. Allowed until removed: {string.Join(", ", NotYetRemoved.Keys.Order())}. "
            + "Put what a build needs in the repository, in our registry pinned by digest, or a pinned download with its checksum.");
    }

    private static IEnumerable<string> Candidates(string root) =>
        Directory.EnumerateFiles(root, "Dockerfile*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github"), "*.yml", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "node_modules" or ".git"))
            // The base images are where installing belongs: built weekly into our registry (base-images.yml).
            .Where(path => !path.StartsWith("base/", StringComparison.Ordinal));

    [GeneratedRegex(@"\b(apt-get|apt|apk|yum|dnf)\s+(-\S+\s+)*(install|add)\b")]
    private static partial Regex Install();
}
