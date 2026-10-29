using System.Text.RegularExpressions;
using Alcifra.Theming;

namespace Alcifra.UnitTests;

// What a build produces is named by the brand (ADR 0994): the scripts and workflows read it through scripts/brand.sh,
// and nothing on the build path spells a package or image name by hand, or a white-label build would ship ours.
public partial class BrandDistributionTests
{
    // A package, image or manual name written out: "Alcifra-$version-…", "Alcifra-*.dmg",
    // "Alcifra-Manual.pdf", "ghcr.io/hebelconsulting/…".
    [GeneratedRegex(@"(Alcifra|Alcifra)-(\$|\*|\{|Manual)|ghcr\.io/hebelconsulting/")]
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
        ".github/workflows/manual.yml",
        "scripts/generate-manual.sh",
        "scripts/edition-manual.sh",
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

    // Who publishes is the brand's to say (ADR 0996): a publishing workflow gated on a repository NAME would publish
    // only from ours, and a white-label edition could never release.
    [Theory]
    [InlineData(".github/workflows/docker-publish.yml")]
    [InlineData(".github/workflows/desktop-packages.yml")]
    public void Publishing_is_gated_on_the_brands_release_repository(string relative)
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root(), relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("scripts/brand-release-role.sh", text);
        Assert.DoesNotContain("github.repository == 'HebelConsulting/SimplArchive'", text);
    }

    // The release carries the manual the image serves (ADR 0997), named and chosen by one script for every edition.
    [Fact]
    public void A_release_carries_the_editions_manual()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "desktop-packages.yml"));
        Assert.Contains("scripts/edition-manual.sh", text);
        Assert.Contains("\"$BRAND_FILE_STEM-Manual.pdf\"", text);
    }

    // Every release publishes the brand's chart (ADR 0998): packaged by the one script that names it from the brand,
    // pushed beside the images under /charts (a chart named like its image would collide with it), and attached.
    [Fact]
    public void A_release_publishes_the_brands_chart()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "docker-publish.yml"));
        Assert.Contains("scripts/package-chart.sh \"$version\"", text);
        Assert.Contains("helm push \"$package\" \"oci://$BRAND_IMAGE_REGISTRY/charts\"", text);
        Assert.Contains("gh release upload \"$GITHUB_REF_NAME\" \"$package\"", text);
    }

    // A white-label edition's CI (ADR 0998): its tests get a stand-in manual under its own name, and its pushes run
    // only the fast tier. Without the first, its manual tests fail on a missing file; without the second, every
    // upstream sync spends the heavy tier on code upstream has already run.
    [Fact]
    public void A_white_label_edition_tests_with_a_stand_in_manual_and_dispatches_its_heavy_tier()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "ci.yml"));
        Assert.Contains("scripts/edition-manual.sh --stand-in", text);
        Assert.Contains("github.event_name == 'push' && needs.fast.outputs.white_label != 'true'", text);
        Assert.Contains("workflow_dispatch:", text);
    }
}
