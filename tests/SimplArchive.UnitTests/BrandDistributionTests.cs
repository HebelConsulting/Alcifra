using System.Text.RegularExpressions;
using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

// What a build produces is named by the brand (ADR 0994): the scripts and workflows read it through scripts/brand.sh,
// and nothing on the build path spells a package or image name by hand, or a white-label build would ship ours.
public partial class BrandDistributionTests
{
    // A package or image name written out: "SimplArchive-$version-…", "SimplArchive-*.dmg", "ghcr.io/hebelconsulting/…".
    [GeneratedRegex(@"SimplArchive-(\$|\*|\{)|ghcr\.io/hebelconsulting/")]
    private static partial Regex HandWrittenName();

    // The build path: the files that produce and publish packages, images and the chart.
    public static TheoryData<string> BuildPath() =>
    [
        "scripts/package-macos-dmg.sh",
        "scripts/package-windows-linux.sh",
        "scripts/package-clients.sh",
        "scripts/package-chart.sh",
        "Dockerfile",
        ".github/workflows/desktop-packages.yml",
        ".github/workflows/docker-publish.yml",
    ];

    [Theory]
    [MemberData(nameof(BuildPath))]
    public void The_build_path_takes_names_from_the_brand(string relative)
    {
        var path = Path.Combine(RepoPaths.Root(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relative} is listed as a build-path file but does not exist.");

        var offenders = File.ReadAllLines(path)
            .Select((line, index) => (line, number: index + 1))
            .Where(l => !l.line.TrimStart().StartsWith('#') && HandWrittenName().IsMatch(l.line))
            // package-chart.sh names the chart's own defaults once, to replace them.
            .Where(l => !l.line.Contains("shipped_images=", StringComparison.Ordinal))
            .Select(l => $"{relative}:{l.number}: {l.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Names written by hand where the brand should name them (source scripts/brand.sh):\n{string.Join("\n", offenders)}");
    }

    [Fact]
    public void The_distribution_identifiers_are_complete()
    {
        var distribution = Brand.Shipped.Distribution;
        Assert.Matches(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", distribution.BundleId);
        Assert.Contains('/', distribution.ReleaseRepository);
        Assert.False(string.IsNullOrWhiteSpace(distribution.ImageRegistry));
        Assert.False(string.IsNullOrWhiteSpace(distribution.ImageName));
        Assert.False(string.IsNullOrWhiteSpace(distribution.ChartName));
        Assert.False(string.IsNullOrWhiteSpace(distribution.Domains.Demo));
    }
}
