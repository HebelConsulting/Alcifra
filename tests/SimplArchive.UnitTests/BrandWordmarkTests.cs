using System.Xml.Linq;
using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

// The brand's wordmark (ADR 0992) is an SVG every surface draws: a browser through an <img>, the desktop through
// its SVG renderer, the manual through Typst. These rules keep it drawable by all three, and identical in each:
// outlined letters (a <text> would depend on a font the viewer may lack), nothing fetched from elsewhere, nothing
// executable, and a viewBox so it scales.
public class BrandWordmarkTests
{
    public static TheoryData<string> Files() => [Brand.Shipped.Wordmark.Light, Brand.Shipped.Wordmark.Dark];

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_wordmark_is_a_self_contained_outlined_svg(string file)
    {
        var path = Path.Combine(RepoPaths.Root(), "brand", "assets", file);
        Assert.True(File.Exists(path), $"brand.json names brand/assets/{file}, which does not exist.");

        var svg = XDocument.Load(path).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(ns + "svg", svg.Name);
        Assert.False(string.IsNullOrWhiteSpace((string?)svg.Attribute("viewBox")), $"{file} has no viewBox, so it cannot scale.");

        var elements = svg.DescendantsAndSelf().Select(e => e.Name.LocalName).ToHashSet();
        foreach (var forbidden in new[] { "text", "script", "image", "foreignObject", "style" })
        {
            Assert.False(elements.Contains(forbidden), $"{file} contains <{forbidden}>: outline the letters and inline nothing.");
        }

        var references = svg.DescendantsAndSelf().SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "href" || a.Value.Contains("url(", StringComparison.Ordinal));
        Assert.Empty(references);
    }

    // The mark stands in for the name only while the product carries the brand's own name; an installation that
    // renamed itself keeps its name as text.
    [Fact]
    public void Only_the_brands_own_name_shows_the_wordmark()
    {
        Assert.True(Brand.ShowsWordmark(Brand.Shipped.Name));
        Assert.False(Brand.ShowsWordmark("Acme Archive"));
    }
}
