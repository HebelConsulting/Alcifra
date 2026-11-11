using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcifra.Theming;

namespace Alcifra.UnitTests;

// The brand is ONE file, brand/brand.json (ADR 0989): what a person reads about the product and how it looks. A
// white-label build replaces that file; these tests make sure the build, the theme and the schema all read the same one.
public class BrandFileTests
{
    [Fact]
    public void The_shipped_brand_is_complete()
    {
        var brand = Brand.Shipped;
        Assert.False(string.IsNullOrWhiteSpace(brand.Name));
        Assert.False(string.IsNullOrWhiteSpace(brand.Publisher));
        Assert.Matches(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", brand.FileStem);
        Assert.Empty(ThemeTokensReader.Validate(ThemeTokensReader.Shipped));
    }

    [Fact]
    public void The_build_and_the_theme_name_the_product_from_the_same_file() =>
        Assert.Equal(Alcifra.Localization.ProductBuild.Name, Brand.Shipped.Name);

    [Fact]
    public void The_shipped_theme_carries_the_brands_name_and_colours()
    {
        Assert.Equal(Brand.Shipped.Name, ThemeTokensReader.Shipped.Name);
        Assert.Equal(Brand.Shipped.Colors.Light, ThemeTokensReader.Shipped.Light);
        Assert.Equal(Brand.Shipped.Colors.Dark, ThemeTokensReader.Shipped.Dark);
    }

    [Fact]
    public void A_field_the_brand_does_not_know_is_refused() =>
        Assert.ThrowsAny<JsonException>(() => Read(Shipped().Replace("\"publisher\":", "\"publsher\":")));

    [Fact]
    public void A_missing_field_is_refused() =>
        Assert.ThrowsAny<JsonException>(() => Read(Regex.Replace(Shipped(), @"""publisher""\s*:\s*""[^""]*"",", string.Empty)));

    [Fact]
    public void The_schema_requires_exactly_the_fields_the_brand_reads()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root(), "brand", "brand.schema.json")));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        var described = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        // A field with a default (colorVariants, #2319) may be left out, so the schema describes it without requiring it.
        var optional = typeof(Brand).GetConstructors().Single().GetParameters().Where(p => p.HasDefaultValue).Select(p => p.Name).ToHashSet();
        var fields = typeof(Brand).GetProperties()
            .Select(p => (p.Name, Json: p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().SingleOrDefault()?.Name))
            .Where(f => f.Json is not null).ToList();
        Assert.Equal(fields.Where(f => !optional.Contains(f.Name)).Select(f => f.Json).ToHashSet(), required);
        Assert.Subset(described, fields.Select(f => f.Json!).ToHashSet());
    }

    private static string Shipped() => File.ReadAllText(Path.Combine(RepoPaths.Root(), "brand", "brand.json"));

    private static Brand Read(string json) => Brand.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    // The imprint every edition shows (ADR 0995): a publisher and an address to reach it.
    [Fact]
    public void The_imprint_names_a_publisher_and_a_support_address()
    {
        var imprint = Brand.Shipped.Imprint;
        Assert.NotEmpty(imprint.Lines);
        Assert.All(imprint.Lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Matches(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", imprint.Contact);
    }
}
