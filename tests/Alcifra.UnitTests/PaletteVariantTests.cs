using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcifra.Theming;
using Microsoft.Extensions.Configuration;

namespace Alcifra.UnitTests;

// Palette variants (#2319, owner 2026-11-11): a brand may carry named palettes beside its own colours, and an operator
// selects one per installation (Branding:Variant). Alcifra Bund: slate by default, blue for blue-light organisations,
// green for the military.
public class PaletteVariantTests
{
    // The shipped brand with a "blue" variant added, so the core's tests do not depend on an edition's brand file.
    private static Brand WithBlue()
    {
        var brand = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root(), "brand", "brand.json")))!.AsObject();
        var blue = JsonNode.Parse(brand["colors"]!.ToJsonString())!.AsObject();
        blue["light"]!["accent"]!["primary"] = "#1F5FAD";
        blue["name"] = new JsonObject { ["en"] = "Blue", ["de"] = "Blau" };
        brand["colorVariants"] = new JsonObject { ["blue"] = blue };
        return Brand.Read(new MemoryStream(Encoding.UTF8.GetBytes(brand.ToJsonString())));
    }

    [Fact]
    public void A_brand_without_variants_still_reads_and_wears_its_own_colours()
    {
        Assert.Equal(Brand.Shipped.Colors, Brand.Shipped.Palette(null));
        Assert.Equal(Brand.Shipped.Colors, Brand.Shipped.Palette("  "));
    }

    [Fact]
    public void A_named_variant_is_worn_and_an_unknown_one_is_refused_naming_the_ones_it_carries()
    {
        var brand = WithBlue();
        Assert.Equal("#1F5FAD", brand.Palette("blue").Light.Accent.Primary);
        Assert.Equal("Blau", brand.ColorVariants!["blue"].Name["de"]);
        var refused = Assert.Throws<ArgumentException>(() => brand.Palette("green"));
        Assert.Contains("blue", refused.Message, StringComparison.Ordinal);
    }

    // Every palette a brand ships is held to the same contrast as its own colours: an edition's CI checks its variants.
    [Fact]
    public void Every_variant_the_shipped_brand_carries_passes_contrast()
    {
        foreach (var (id, variant) in Brand.Shipped.ColorVariants ?? new Dictionary<string, BrandColorVariant>())
        {
            var failures = ThemeTokensReader.Validate(new ThemeTokens(Brand.Shipped.Name, variant.Light, variant.Dark)).ToList();
            Assert.True(failures.Count == 0, $"Palette '{id}': {string.Join("; ", failures)}");
            Assert.True(variant.Name.ContainsKey("en"), $"Palette '{id}' has no English name.");
        }
    }

    [Fact]
    public void An_installation_without_the_setting_wears_the_brand_and_a_wrong_id_stops_the_start()
    {
        var none = Alcifra.Api.Branding.InstallationPalette.From(new ConfigurationBuilder().Build());
        Assert.Null(none.Variant);
        Assert.Equal(ThemeTokensReader.Shipped, none.Tokens);

        var wrong = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Branding:Variant"] = "no-such-palette" }).Build();
        var refused = Assert.Throws<InvalidOperationException>(() => Alcifra.Api.Branding.InstallationPalette.From(wrong));
        Assert.StartsWith("Branding:Variant:", refused.Message, StringComparison.Ordinal);
    }

    // An operator's custom/theme.json is laid over the SELECTED palette: what it leaves out stays the variant's.
    [Fact]
    public void A_custom_theme_is_laid_over_the_selected_palette()
    {
        var blue = WithBlue().Palette("blue");
        var basis = new ThemeTokens("Test", blue.Light, blue.Dark);
        var load = ThemeTokensReader.Load("""{ "light": { "semantic": { "danger": "#991B1B" } }, "dark": { "semantic": { "danger": "#FCA5A5" } } }""", basis);
        Assert.Equal("#1F5FAD", load.Tokens.Light.Accent.Primary);
        Assert.True(load.Applied, string.Join("; ", load.Notes));
        Assert.Equal("#991B1B", load.Tokens.Light.Semantic.Danger);
    }

    [Fact]
    public void The_selected_palettes_manual_is_served_when_the_build_carries_it_and_the_brands_own_otherwise()
    {
        var stem = Alcifra.Theming.Brand.Shipped.FileStem;
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "download", "manual"));
            File.WriteAllBytes(Path.Combine(root.FullName, "download", "manual", $"{stem}-Manual-blue.pdf"), [1]);
            using var webRoot = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root.FullName);
            var tokens = Alcifra.Theming.ThemeTokensReader.Shipped;

            Assert.Equal($"download/manual/{stem}-Manual-blue.pdf", new Alcifra.Api.Branding.InstallationPalette("blue", tokens).ManualPath(webRoot));
            Assert.Equal(Alcifra.Api.Branding.InstallationPalette.DefaultManualPath,
                new Alcifra.Api.Branding.InstallationPalette("green", tokens).ManualPath(webRoot));
            Assert.Equal(Alcifra.Api.Branding.InstallationPalette.DefaultManualPath,
                new Alcifra.Api.Branding.InstallationPalette(null, tokens).ManualPath(webRoot));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
