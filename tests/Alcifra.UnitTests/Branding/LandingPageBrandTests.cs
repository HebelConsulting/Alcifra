using Alcifra.Api.Documents;

namespace Alcifra.UnitTests.Branding;

// The external-link landing page is the one surface a stranger with a browser reads, and it is anonymous (ADR 0546).
// The runtime brand's rule is absolute there (ADR 1103, #2408): the tenant's COLOURS may appear, its mark, its
// assets and its name never. These pin the page's own contribution — the tenant's palette blocks and nothing else.
public sealed class LandingPageBrandTests
{
    private const string TenantCss = ":root {\n  --sa-accent: #1F5FAD;\n}\n";

    [Fact]
    public void A_tenants_palette_is_laid_over_the_linked_installation_sheet()
    {
        var html = ExternalLinkPage.Live(
            "report.pdf", DateTimeOffset.UtcNow.AddDays(1), "/api/external-links/t/content", paletteCss: TenantCss);

        // Colours: the tenant's, after the installation's own sheet so its rules win at equal specificity.
        Assert.Contains("--sa-accent: #1F5FAD", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/api/theme.css\"", html, StringComparison.Ordinal);

        // The brand itself: never. The endpoint is not named, no asset is referenced, and this page has no mark at
        // all — the rule is structural on this surface, not a display preference.
        Assert.DoesNotContain("/api/brand", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wordmark", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_pack_the_page_adds_no_tenant_style_block()
    {
        var html = ExternalLinkPage.Live(
            "report.pdf", DateTimeOffset.UtcNow.AddDays(1), "/api/external-links/t/content");

        // The page's own inline stylesheet mentions the variables only as `var(--sa-accent, …)`; a DEFINITION of one
        // would be the tenant's palette block.
        Assert.DoesNotContain("--sa-accent:", html, StringComparison.Ordinal);
        Assert.Contains("var(--sa-accent,", html, StringComparison.Ordinal);
    }
}
