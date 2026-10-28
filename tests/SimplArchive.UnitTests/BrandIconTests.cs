using System.Xml.Linq;
using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

// The brand's own app icon (ADR 0993), when brand.json names one: an SVG every icon size is rendered from, so it
// keeps the wordmark's rules, and it must be square, or every launcher would squash it.
public class BrandIconTests
{
    [Fact]
    public void A_named_icon_is_a_square_self_contained_svg()
    {
        if (Brand.Shipped.Icon is not { } file)
        {
            return; // the drawn cabinet in the brand's colours: nothing to check here (IconArtFollowsTheBrandTests)
        }

        BrandWordmarkTests.AssertSelfContainedSvg(file);

        var viewBox = ((string)XDocument.Load(Path.Combine(RepoPaths.Root(), "brand", "assets", file)).Root!.Attribute("viewBox")!)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(Math.Abs(viewBox[2] - viewBox[3]) < 0.01, $"{file} is {viewBox[2]}x{viewBox[3]}; an app icon must be square.");
    }
}
