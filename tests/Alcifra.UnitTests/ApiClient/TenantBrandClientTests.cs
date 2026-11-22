using System.Text.Json;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests.ApiClient;

// The shared runtime-brand client (ADR 1103, Mobile #313): it reads what the server RESOLVED — the fallback order
// lives there — and follows only advertised rels. Absence answers null, which every host reads as "the installation's
// brand stays".
[Collection(ApiClientSettingsCollection.Name)]
public sealed class TenantBrandClientTests : ApiClientSettingsTest
{

    [Fact]
    public async Task The_brand_is_read_through_the_roots_rel_with_its_palettes_and_tokens()
    {
        using var server = new LoopbackServer((method, path, _) => path switch
        {
            "/api" => (200, Root()),
            "/api/brand" => (200, BrandJson()),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        var brand = await new TenantBrandClient(new ApiCore("access-1")).ReadAsync();

        Assert.NotNull(brand);
        Assert.Equal("bund", brand.PackId);
        Assert.Equal("Alcifra Bund", brand.ProductName);
        Assert.Equal("blue", brand.PaletteId);
        Assert.True(brand.CanChoosePalette);
        Assert.Equal(["blue", "green"], brand.Palettes.Select(palette => palette.Id));
        Assert.Equal("Blau", brand.Palettes[0].Name["de"]);
        Assert.Equal("v1", brand.Version);

        var tokens = TenantBrandClient.Tokens(brand);
        Assert.NotNull(tokens);
        Assert.Equal("#1F5FAD", tokens.Light.Accent.Primary);

        // One read of the root, one of the brand — no composed addresses.
        Assert.Equal(["/api", "/api/brand"], server.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task Without_the_rel_the_answer_is_null_and_nothing_else_is_fetched()
    {
        using var server = new LoopbackServer((_, path, _) => path switch
        {
            "/api" => (200, """{"links":[{"rel":"theme","href":"/api/theme","method":"GET"}]}"""),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        Assert.Null(await new TenantBrandClient(new ApiCore("access-1")).ReadAsync());
        Assert.Equal(["/api"], server.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task A_declared_asset_is_read_with_the_token_and_an_undeclared_one_is_not_asked_for()
    {
        using var server = new LoopbackServer((_, path, _) => path switch
        {
            "/api" => (200, Root()),
            "/api/brand" => (200, BrandJson()),
            "/api/brand/assets/wordmarkLight" => (200, "<svg/>"),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        var client = new TenantBrandClient(new ApiCore("access-1"));

        var asset = await client.ReadAssetAsync("wordmarkLight");
        Assert.NotNull(asset);
        Assert.Equal("<svg/>", System.Text.Encoding.UTF8.GetString(asset.Value.Content));

        // It is the DECLARED address that was fetched — the manifest is the whitelist, not a path convention.
        Assert.Contains(server.Requests, request => request.Path == "/api/brand/assets/wordmarkLight");

        // The manifest is the whitelist: a name it does not declare never becomes a request.
        Assert.Null(await client.ReadAssetAsync("private.txt"));
        Assert.DoesNotContain(server.Requests, request => request.Path.Contains("private.txt"));
    }

    [Fact]
    public async Task The_palette_choice_is_written_at_the_advertised_rel_with_the_advertised_method()
    {
        using var server = new LoopbackServer((method, path, _) => (method, path) switch
        {
            ("GET", "/api") => (200, Root()),
            ("GET", "/api/brand") => (200, BrandJson()),
            ("PUT", "/api/brand/palette") => (204, string.Empty),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        var client = new TenantBrandClient(new ApiCore("access-1"));

        Assert.True(await client.SelectPaletteAsync("green"));

        var write = server.Requests.Single(request => request.Method == "PUT");
        Assert.Equal("/api/brand/palette", write.Path);
        Assert.Equal("green", JsonDocument.Parse(write.Body).RootElement.GetProperty("paletteId").GetString());
    }

    [Fact]
    public async Task Without_the_select_palette_rel_no_choice_is_offered_and_nothing_is_written()
    {
        using var server = new LoopbackServer((_, path, _) => path switch
        {
            "/api" => (200, Root()),
            "/api/brand" => (200, BrandJson(includeChoice: false)),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        Assert.False(await new TenantBrandClient(new ApiCore("access-1")).SelectPaletteAsync("green"));
        Assert.DoesNotContain(server.Requests, request => request.Method != "GET");
    }

    private static string Root() =>
        """{"serverVersion":"0.62.7","links":[{"rel":"brand","href":"/api/brand","method":"GET"}]}""";

    // A complete palette, because the theme reader — the same one the pack store and the clients use — accepts
    // nothing less; and the accent is the piece the test asserts.
    private static string BrandJson(bool includeChoice = true)
    {
        var light = new
        {
            accent = new { primary = "#1F5FAD", hover = "#0D6A62", onPrimary = "#FFFFFF", text = "#0B5D56", tint = "#E6F2F1", selection = "#CCE7E4" },
            semantic = new { danger = "#B91C1C", success = "#15803D", warning = "#A16207" },
            neutral = new { canvas = "#FAFAFC", surface = "#FFFFFF", sunken = "#F1F2F5", hairline = "#E6E7EC", text = "#14161C", textSecondary = "#5A5F6E", textFaint = "#898F9E", folder = "#D9A441" },
        };
        var dark = new
        {
            accent = new { primary = "#4C8FD9", hover = "#5BD9CE", onPrimary = "#06192F", text = "#9CC7F0", tint = "#123B36", selection = "#1B4B45" },
            semantic = new { danger = "#F87171", success = "#4ADE80", warning = "#FBBF24" },
            neutral = new { canvas = "#0F1115", surface = "#171A20", sunken = "#20242C", hairline = "#2C313B", text = "#E8EAEF", textSecondary = "#A6ACB9", textFaint = "#767C89", folder = "#E0B45C" },
        };

        var links = new List<object>
        {
            new { rel = "asset", href = "/api/brand/assets/wordmarkLight", method = "GET" },
        };
        if (includeChoice)
        {
            links.Add(new { rel = "select-palette", href = "/api/brand/palette", method = "PUT" });
        }

        return JsonSerializer.Serialize(new
        {
            packId = "bund",
            productName = "Alcifra Bund",
            paletteId = "blue",
            canChoosePalette = includeChoice,
            palettes = new object[]
            {
                new { id = "blue", name = new { en = "Blue", de = "Blau" } },
                new { id = "green", name = new { en = "Green", de = "Grün" } },
            },
            theme = new { light, dark },
            version = "v1",
            links,
        });
    }
}
