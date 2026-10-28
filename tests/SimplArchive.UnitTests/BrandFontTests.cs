using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

// The brand's typefaces (ADR 0991): every font the build ships lives in brand/assets/fonts, carries a licence we may
// ship under, and comes in the files each consumer reads: static faces for the desktop and the manual, variable
// fonts for the web. A white-label fork swaps these files; this is what tells it which ones it owes.
public class BrandFontTests
{
    // Only licences that allow bundling the font into an Apache-2.0 product and its packages.
    private static readonly string[] PermittedLicences = ["SIL Open Font License", "Apache License"];

    private static string FontsDirectory => Path.Combine(RepoPaths.Root(), "brand", "assets", "fonts");

    private static IReadOnlyList<string> FamilyNames =>
        [.. new[] { Brand.Shipped.Fonts.Ui, Brand.Shipped.Fonts.Document }.Distinct()];

    public static TheoryData<string> Families() => [.. FamilyNames];

    [Theory]
    [MemberData(nameof(Families))]
    public void Each_family_ships_under_a_permitted_licence(string family)
    {
        var licence = Path.Combine(FontsDirectory, $"{BrandFonts.FileStem(family)}-LICENSE.txt");
        Assert.True(File.Exists(licence), $"{family} has no licence file at brand/assets/fonts/{Path.GetFileName(licence)}.");

        var text = File.ReadAllText(licence);
        Assert.True(
            PermittedLicences.Any(text.Contains),
            $"{Path.GetFileName(licence)} is neither the SIL Open Font License nor Apache-2.0, so the font cannot ship.");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Each_family_has_static_faces_for_the_desktop_and_the_manual(string family) =>
        Assert.True(
            File.Exists(Path.Combine(FontsDirectory, $"{BrandFonts.FileStem(family)}-Regular.ttf")),
            $"{family} has no {BrandFonts.FileStem(family)}-Regular.ttf: the desktop and the manual would fall back to another font.");

    [Fact]
    public void The_ui_family_has_the_variable_fonts_the_web_loads()
    {
        var stem = BrandFonts.FileStem(Brand.Shipped.Fonts.Ui);
        foreach (var file in new[] { $"{stem}Variable.woff2", $"{stem}Variable-Italic.woff2" })
        {
            Assert.True(
                File.Exists(Path.Combine(FontsDirectory, file)),
                $"brand/assets/fonts/{file} is missing, and /api/theme.css names it: the web would fall back to another font.");
        }
    }

    // A file nobody named is a font shipped without a checked licence.
    [Fact]
    public void Every_file_belongs_to_a_family_the_brand_names()
    {
        var stems = FamilyNames.Select(BrandFonts.FileStem).ToList();
        var strays = Directory.EnumerateFiles(FontsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !stems.Any(stem => name!.StartsWith(stem, StringComparison.Ordinal)))
            .ToList();

        Assert.True(strays.Count == 0, $"brand/assets/fonts holds files of no named family: {string.Join(", ", strays)}.");
    }
}
