using System.Globalization;
using System.Text.RegularExpressions;
using SimplArchive.Localization;

namespace SimplArchive.UnitTests;

// The product's name is ONE value (core #1117, Mobile #187): every text that names the product says {Product}, and
// Strings fills in the name a host set, Alcifra by default. A literal name in a text would survive a rename, or a change
// of the wordmark's casing, unnoticed.
public class ProductNameTests
{
    // Stored names and identifiers that keep their spelling: the manuals folder and the documentation mask are seeded
    // data that a text must name as they are, and the webhook's header is a protocol identifier.
    private static readonly HashSet<string> NamesStoredData = ["TenantWebhookUrlInfo", "TenantStandardRepositoryInfo", "ApiErrCoreOwnedMask", "WkMaskDocumentation"];

    private static readonly Regex Entry = new(@"<data name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void A_text_names_the_product_by_default_as_Alcifra() =>
        Assert.Equal("About Alcifra", Strings.Get("AboutHeading", CultureInfo.InvariantCulture));

    [Fact]
    public void A_host_sets_the_name_once_and_every_text_says_it()
    {
        try
        {
            Strings.ProductName = "Example Archive";
            Assert.Equal("About Example Archive", Strings.Get("AboutHeading", CultureInfo.InvariantCulture));
            Assert.Equal("Über Example Archive", Strings.Get("AboutHeading", CultureInfo.GetCultureInfo("de")));
        }
        finally
        {
            Strings.ProductName = Strings.DefaultProductName;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("es")]
    public void No_text_spells_the_product_name_out(string language)
    {
        var file = Path.Combine(RepoPaths.Root(), "src", "SimplArchive.Localization", language.Length == 0 ? "Strings.resx" : $"Strings.{language}.resx");
        var literal = Entry.Matches(File.ReadAllText(file))
            .Where(m => !NamesStoredData.Contains(m.Groups[1].Value))
            .Where(m => m.Groups[2].Value.Contains("SimplArchive", StringComparison.Ordinal)
                || m.Groups[2].Value.Contains(Strings.DefaultProductName, StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(literal.Count == 0,
            $"These texts spell the product's name out instead of saying {Strings.ProductToken}, so a rename misses them:\n  "
            + string.Join("\n  ", literal));
    }
}
