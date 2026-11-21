using Alcifra.Application.Branding;

namespace Alcifra.UnitTests.Branding;

// The fallback order a signed-in caller's brand resolves through (ADR 1103, #2408): user override (only while the
// tenant allows it) → tenant palette → the pack's default → the installation's own brand. The clients never
// re-derive this; a stored choice that no longer exists must not break the page it paints.
public sealed class TenantBrandResolverTests
{
    private static readonly BrandPalette Default = new(null, new Dictionary<string, string>(), Theme("primary", "#000000"), Theme("primary", "#111111"));

    private static BrandPalette Variant(string id, string colour) =>
        new(id, new Dictionary<string, string> { ["en"] = id }, Theme("primary", colour), Theme("primary", colour));

    private static BrandTheme Theme(string key, string value) =>
        new(new Dictionary<string, IReadOnlyDictionary<string, string>> { ["accent"] = new Dictionary<string, string> { [key] = value } });

    private sealed class Store(params BrandPack[] packs) : IBrandPackStore
    {
        public IReadOnlyList<string> AvailablePackIds => packs.Select(pack => pack.Id).ToList();

        public BrandPack? Find(string? packId) => packs.FirstOrDefault(pack => pack.Id == packId);

        public BrandAssetSource? Open(string packId, string assetName) => null;
    }

    private static BrandPack Pack(params BrandPalette[] variants) =>
        new("bund", "Alcifra Bund", Default, variants, new Dictionary<string, string>(), "v1");

    private static TenantBrandResolver Resolver(params BrandPack[] packs) => new(new Store(packs), "Alcifra");

    [Fact]
    public void A_tenant_without_a_pack_wears_the_installations_own_brand()
    {
        var brand = Resolver(Pack(Variant("blue", "#1F5FAD"))).Resolve(null, "blue", allowUserOverride: true, userPalette: "blue");

        Assert.Null(brand.PackId);
        Assert.Equal("Alcifra", brand.ProductName);
        Assert.Empty(brand.Palettes);
        Assert.False(brand.CanChoosePalette);
        Assert.Null(brand.Palette);
    }

    [Fact]
    public void A_pack_the_installation_does_not_carry_falls_back_to_the_installations_brand()
    {
        // The load already warned about the missing pack; the page keeps painting.
        var brand = Resolver().Resolve("gone", "blue", allowUserOverride: true, userPalette: "green");

        Assert.Null(brand.PackId);
        Assert.Null(brand.Palette);
    }

    [Fact]
    public void The_tenant_palette_applies_and_the_packs_own_colours_are_the_fallback()
    {
        var resolver = Resolver(Pack(Variant("blue", "#1F5FAD"), Variant("green", "#2F6B3A")));

        Assert.Equal("blue", resolver.Resolve("bund", "blue", allowUserOverride: false, userPalette: null).PaletteId);
        Assert.Null(resolver.Resolve("bund", tenantPalette: null, allowUserOverride: false, userPalette: null).PaletteId);
    }

    [Fact]
    public void A_users_override_applies_only_while_the_tenant_allows_it()
    {
        var resolver = Resolver(Pack(Variant("blue", "#1F5FAD"), Variant("green", "#2F6B3A")));

        Assert.Equal("green", resolver.Resolve("bund", "blue", allowUserOverride: true, userPalette: "green").PaletteId);
        Assert.Equal("blue", resolver.Resolve("bund", "blue", allowUserOverride: false, userPalette: "green").PaletteId);
    }

    [Fact]
    public void A_palette_the_current_brand_no_longer_carries_falls_back_silently()
    {
        var resolver = Resolver(Pack(Variant("blue", "#1F5FAD")));

        // A tenant switched brand and left a stored palette behind — a user's and the tenant's both. Both fall
        // through to the pack's OWN colours (a null id), never to a variant nobody chose.
        Assert.Null(resolver.Resolve("bund", tenantPalette: "purple", allowUserOverride: true, userPalette: "purple").PaletteId);
        Assert.Null(resolver.Resolve("bund", tenantPalette: "purple", allowUserOverride: false, userPalette: null).PaletteId);
    }

    [Fact]
    public void A_palette_choice_is_offered_only_when_it_would_do_something()
    {
        var two = Resolver(Pack(Variant("blue", "#1F5FAD"), Variant("green", "#2F6B3A")));
        var one = Resolver(Pack(Variant("blue", "#1F5FAD")));

        Assert.True(two.Resolve("bund", null, allowUserOverride: true, userPalette: null).CanChoosePalette);
        Assert.False(two.Resolve("bund", null, allowUserOverride: false, userPalette: null).CanChoosePalette);
        Assert.False(one.Resolve("bund", null, allowUserOverride: true, userPalette: null).CanChoosePalette);
    }
}
