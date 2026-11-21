using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

/// <summary>
/// The runtime brand endpoint (ADR 1103, #2408). Its FIRST property is the legal rule, and this pins it at the door:
/// no token, no tenant brand — the anonymous surfaces cannot fetch one even if a renderer ever asked.
/// </summary>
/// <remarks>
/// The shared installation mounts one fixture pack (see <see cref="BrandPackFixture"/>) but NO tenant carries it
/// here: an unassigned tenant is every ordinary customer installation, which is what these cases pin — the
/// installation's own brand, and a palette choice refused by name. The pack-bearing cases live in
/// <see cref="TenantBrandTests"/>, which assigns the pack to a tenant of its own.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class BrandEndpointTests(E2EApiFactory factory)
{
    [Fact]
    public async Task Without_a_token_the_brand_endpoint_refuses()
    {
        using var anonymous = factory.CreateClient();

        var brand = await anonymous.GetAsync("/api/brand");
        var asset = await anonymous.GetAsync("/api/brand/assets/wordmarkLight");

        Assert.Equal(HttpStatusCode.Unauthorized, brand.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asset.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_caller_of_a_packless_installation_wears_the_installations_brand()
    {
        var client = await SignedInAsync();

        var brand = await client.GetFromJsonAsync<JsonElement>("/api/brand");

        Assert.Equal(JsonValueKind.Null, brand.GetProperty("packId").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(brand.GetProperty("productName").GetString()));
        Assert.False(brand.GetProperty("canChoosePalette").GetBoolean());
        Assert.Empty(brand.GetProperty("palettes").EnumerateArray());
    }

    [Fact]
    public async Task Choosing_a_palette_without_a_brand_pack_is_refused_by_name()
    {
        var client = await SignedInAsync();

        using var response = await client.PutAsJsonAsync("/api/brand/palette", new { paletteId = "blue" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NO_BRAND_PACK", problem.GetProperty("errorCode").GetString());
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"brand-{Guid.NewGuid():N}@e2e.local";
        const string password = "brand1234";
        await factory.SeedUserAsync(tenantId, email, password, "Brand User");
        return factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
    }
}
