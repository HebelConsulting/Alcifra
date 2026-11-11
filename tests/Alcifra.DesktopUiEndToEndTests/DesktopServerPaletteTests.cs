using Alcifra.DesktopClient.Services;
using Alcifra.Theming;

namespace Alcifra.UiEndToEndTests;

// A server profile with no style of its own wears the SERVER's palette (#2319, owner 2026-11-11): the brand's colours or
// the variant its operator selected, read from the anonymous theme rel when the server is chosen. A chosen style wins.
[Collection(UiCollection.Name)]
public class DesktopServerPaletteTests(SelfHostedAppFixture app)
{
    [Fact]
    public void Only_a_profile_without_a_style_of_its_own_follows_the_server()
    {
        Assert.True(ServerPalette.Follows(null));
        Assert.True(ServerPalette.Follows(""));
        Assert.True(ServerPalette.Follows(ThemeCatalog.DefaultId));
        Assert.False(ServerPalette.Follows("indigo"));
    }

    [Fact]
    public async Task The_servers_palette_is_read_from_its_theme_rel()
    {
        var palette = await ServerPalette.ReadAsync(app.BaseUrl);
        Assert.NotNull(palette);
        Assert.Equal(ThemeTokensReader.Shipped.Light.Accent.Primary, palette!.Tokens.Light.Accent.Primary);
        Assert.Equal(ThemeTokensReader.Shipped.Dark.Accent.Primary, palette.Tokens.Dark.Accent.Primary);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_leaves_the_shipped_design()
    {
        Assert.Null(await ServerPalette.ReadAsync("http://127.0.0.1:9"));
    }
}
