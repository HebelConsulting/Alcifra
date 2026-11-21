using Alcifra.Theming;

namespace Alcifra.UnitTests.Branding;

// A brand pack's palette is stated in the theme vocabulary and read with ONE parser (ADR 1103, #2408): complete,
// every value a colour, and refused by name when it is neither — the refusal is how a broken pack is left out at
// load, rather than painting half a brand or failing a stranger's landing page.
public sealed class PackPaletteTests
{
    [Fact]
    public void A_complete_palette_becomes_the_themes_tokens_with_its_own_neutrals()
    {
        var tokens = ThemeTokensReader.FromGroups("Bund", Palette("#1F5FAD"), Palette("#1F5FAD"));

        Assert.Equal("Bund", tokens.Name);
        Assert.Equal("#1F5FAD", tokens.Light.Accent.Primary);
        Assert.Equal("#1F5FAD", tokens.Dark.Accent.Primary);

        // The pack's neutrals APPLY — unlike a custom theme file's, which are ignored because they only override our
        // own design. A pack is the brand, not an override of ours.
        Assert.Equal("#FAFAFC", tokens.Light.Neutral.Canvas);
    }

    [Fact]
    public void A_palette_missing_a_group_is_refused_naming_the_first_missing_token()
    {
        var incomplete = Palette("#1F5FAD");
        incomplete.Remove("neutral");

        var error = Assert.Throws<BrandPaletteInvalidException>(
            () => ThemeTokensReader.FromGroups("Bund", incomplete, Palette("#1F5FAD")));

        Assert.Contains("light.neutral.canvas", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_is_not_a_colour_is_refused()
    {
        Assert.Throws<BrandPaletteInvalidException>(
            () => ThemeTokensReader.FromGroups("Bund", Palette("teal"), Palette("#1F5FAD")));
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Palette(string primary) => new()
    {
        ["accent"] = new Dictionary<string, string>
        {
            ["primary"] = primary,
            ["hover"] = "#0D6A62",
            ["onPrimary"] = "#FFFFFF",
            ["text"] = "#0B5D56",
            ["tint"] = "#E6F2F1",
            ["selection"] = "#CCE7E4",
        },
        ["semantic"] = new Dictionary<string, string>
        {
            ["danger"] = "#B91C1C",
            ["success"] = "#15803D",
            ["warning"] = "#A16207",
        },
        ["neutral"] = new Dictionary<string, string>
        {
            ["canvas"] = "#FAFAFC",
            ["surface"] = "#FFFFFF",
            ["sunken"] = "#F1F2F5",
            ["hairline"] = "#E6E7EC",
            ["text"] = "#14161C",
            ["textSecondary"] = "#5A5F6E",
            ["textFaint"] = "#898F9E",
            ["folder"] = "#D9A441",
        },
    };
}
