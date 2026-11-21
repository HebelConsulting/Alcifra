using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alcifra.EndToEndTests;

// The runtime brand end to end (ADR 1103, #2408): a tenant assigned a MOUNTED pack wears it after sign-in, the
// palette choice is the caller's own, and the anonymous external-link landing page may wear the tenant's COLOURS
// but never its mark or its name — the legal rule, on the one page a stranger reads.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class TenantBrandTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_mounted_pack_paints_the_tenant_after_sign_in()
    {
        var (api, tenantId, _) = await SeedBrandTenantAsync();
        await factory.AssignBrandPackAsync(tenantId, BrandPackFixture.PackId, paletteId: "blue", allowUserPaletteOverride: true);

        var brand = await TestJson.Get(api, "/api/brand");

        Assert.Equal(BrandPackFixture.PackId, brand.GetProperty("packId").GetString());
        Assert.Equal(BrandPackFixture.ProductName, brand.GetProperty("productName").GetString());
        Assert.Equal("blue", brand.GetProperty("paletteId").GetString());
        Assert.True(brand.GetProperty("canChoosePalette").GetBoolean());
        Assert.Equal(
            new[] { "blue", "green" },
            brand.GetProperty("palettes").EnumerateArray()
                .Select(palette => palette.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal));

        // The effective palette's tokens, exactly as the pack file writes them — the client paints from these.
        Assert.Equal(
            BrandPackFixture.DefaultAccent,
            brand.GetProperty("theme").GetProperty("light").GetProperty("accent").GetProperty("primary").GetString());

        // The asset is reachable by the rel it declares, and only for names the manifest lists.
        var wordmark = brand.GetProperty("links").EnumerateArray()
            .Single(link => link.GetProperty("rel").GetString() == "asset"
                && link.GetProperty("href").GetString()!.EndsWith("/wordmarkLight", StringComparison.Ordinal));
        using var asset = await api.GetAsync(wordmark.GetProperty("href").GetString());
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("image/svg+xml", asset.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await asset.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var undeclared = await api.GetAsync("/api/brand/assets/private.txt");
        Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
    }

    [Fact]
    public async Task The_root_offers_the_brand_only_once_a_tenant_is_known()
    {
        using var anonymous = factory.CreateClient();
        var anonymousRoot = await anonymous.GetFromJsonAsync<JsonElement>("/api");
        Assert.DoesNotContain("brand", RelsOf(anonymousRoot));

        var (api, _, _) = await SeedBrandTenantAsync();
        var signedInRoot = await api.GetFromJsonAsync<JsonElement>("/api");
        Assert.Contains("brand", RelsOf(signedInRoot));
    }

    [Fact]
    public async Task The_palette_choice_is_the_callers_own_and_resolves_through_the_tenant_default()
    {
        var (api, tenantId, _) = await SeedBrandTenantAsync();
        await factory.AssignBrandPackAsync(tenantId, BrandPackFixture.PackId, paletteId: "blue", allowUserPaletteOverride: true);

        (await api.PutAsJsonAsync("/api/brand/palette", new { paletteId = "green" })).EnsureSuccessStatusCode();

        var chosen = await TestJson.Get(api, "/api/brand");
        Assert.Equal("green", chosen.GetProperty("paletteId").GetString());
        Assert.Equal(
            BrandPackFixture.GreenAccent,
            chosen.GetProperty("theme").GetProperty("light").GetProperty("accent").GetProperty("primary").GetString());

        // Clearing the override falls back to the TENANT's palette, not to the pack's own colours: the user's choice
        // is theirs, and "default" means what the administrator configured.
        (await api.PutAsJsonAsync("/api/brand/palette", new { paletteId = (string?)null })).EnsureSuccessStatusCode();

        var cleared = await TestJson.Get(api, "/api/brand");
        Assert.Equal("blue", cleared.GetProperty("paletteId").GetString());
    }

    [Fact]
    public async Task The_landing_page_wears_the_tenants_colours_but_never_its_mark()
    {
        var (api, tenantId, docId) = await SeedBrandTenantAsync(seedShareableDocument: true);
        await factory.AssignBrandPackAsync(tenantId, BrandPackFixture.PackId, paletteId: "blue", allowUserPaletteOverride: true);

        // The SHARING user's own palette must not reach a stranger: the page wears the tenant's colours, not one
        // person's private display preference.
        (await api.PutAsJsonAsync("/api/brand/palette", new { paletteId = "green" })).EnsureSuccessStatusCode();

        var link = await TestJson.Post(api, $"/api/documents/{docId}/external-links", new { });
        var path = new Uri(link.GetProperty("url").GetString()!).PathAndQuery;

        using var anonymous = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        var page = await anonymous.SendAsync(request);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();

        // Colours: yes — the tenant's own, from its configured palette.
        Assert.Contains($"--sa-accent: {BrandPackFixture.DefaultAccent}", html, StringComparison.Ordinal);
        Assert.DoesNotContain(BrandPackFixture.GreenAccent, html, StringComparison.OrdinalIgnoreCase);

        // The brand itself: never. No asset, no wordmark, and not the tenant's product name — the footer keeps the
        // installation's, because the recipient has no way to know any other name.
        Assert.DoesNotContain("/api/brand", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wordmark", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BrandPackFixture.ProductName, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_alcifra_command_assigns_and_clears_a_pack_through_the_advertised_rels()
    {
        var (_, tenantId, _) = await SeedBrandTenantAsync();
        var (clientId, secret) = await factory.SeedPlatformAdministratorAsync();
        using var admin = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var api = new AlcifraApi(admin);

        // The command resolves the tenant through the root's `tenants` collection and writes at the row's `assign`
        // rel — nothing is composed, which is what makes a route move invisible to the tool (ADR 0543/0871).
        var written = await TenantBrandSurface.SetAsync(
            api, tenantId, BrandPackFixture.PackId, "green", allowUserOverride: true, CancellationToken.None);

        Assert.Equal(BrandPackFixture.PackId, written.GetProperty("packId").GetString());
        Assert.Equal("green", written.GetProperty("paletteId").GetString());
        Assert.True(written.GetProperty("allowUserPaletteOverride").GetBoolean());

        var (read, _) = await TenantBrandSurface.ReadAsync(api, tenantId, CancellationToken.None);
        Assert.Equal("green", read.GetProperty("paletteId").GetString());

        // `none` returns the tenant to the installation's brand, and the palette goes WITH the pack: a preference
        // that belongs to a pack nothing can resolve is not left behind.
        var cleared = await TenantBrandSurface.SetAsync(api, tenantId, "none", null, allowUserOverride: false, CancellationToken.None);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("packId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("paletteId").ValueKind);
    }

    private static IEnumerable<string> RelsOf(JsonElement root) =>
        root.GetProperty("links").EnumerateArray().Select(link => link.GetProperty("rel").GetString() ?? string.Empty);

    private async Task<(HttpClient Api, Guid TenantId, Guid DocumentId)> SeedBrandTenantAsync(
        bool seedShareableDocument = false)
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);

        var email = $"kiosk-{Guid.NewGuid():N}@e2e.local";
        const string password = "kiosk1234";
        await factory.SeedUserAsync(tenantId, email, password, "Kiosk User",
            canManageRepositories: true, canCreateExternalLink: true, isTenantAdmin: true);

        var api = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));

        var docId = Guid.Empty;
        if (seedShareableDocument)
        {
            await SetAllowExternalLinksAsync(tenantId);

            var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Kiosk {Guid.NewGuid():N}" }))
                .GetProperty("id").GetGuid();
            docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = "kiosk-doc" }))
                .GetProperty("id").GetGuid();

            var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                    new ByteArrayContent(Encoding.UTF8.GetBytes("kiosk content")))).EnsureSuccessStatusCode();
            }

            (await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { }))
                .EnsureSuccessStatusCode();
        }

        return (api, tenantId, docId);
    }

    private async Task SetAllowExternalLinksAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(t => t.Id == tenantId);
        tenant.AllowExternalLinks = true;
        await db.SaveChangesAsync();
    }
}
