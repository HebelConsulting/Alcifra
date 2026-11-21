using Alcifra.Application.Branding;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Theming;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.Api.Branding;

/// <summary>
/// The tenant colours an external link's landing page may wear (ADR 1103, #2408): the pack's palette as the tenant
/// configured it, emitted as CSS custom properties — and nothing else. No logo, no wordmark, no product name.
/// </summary>
/// <remarks>
/// <para>
/// The landing page is anonymous by nature (ADR 0546): a stranger with a browser and no account opens the link.
/// The absolute rule is that no tenant BRAND may appear over an anonymous connection — and colours are the one part
/// of a brand that is not one: they cannot identify whose archive this is, while a mark is exactly that. So the
/// page keeps the installation's structure, footer and name, links the installation's own sheet, and this service
/// contributes only the four colour blocks the theme emitter writes.
/// </para>
/// <para>
/// Resolved WITHOUT the sharing user's own palette override: the colour a stranger sees is the tenant's, not one
/// person's private display preference. A pack that cannot paint is a colour wrong, never a broken page — every
/// refusal falls back to the installation's sheet, which the page has already linked.
/// </para>
/// </remarks>
public sealed class LandingPalette(AlcifraDbContext db, TenantBrandResolver resolver)
{
    public async Task<string> CssAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            // IgnoreQueryFilters like the redemption's own lookup: the caller is anonymous and the tenant is known
            // only from the link's row.
            var tenant = await db.Tenants
                .IgnoreQueryFilters(["TenantFilter"])
                .AsNoTracking()
                .Where(candidate => candidate.Id == tenantId)
                .Select(candidate => new { candidate.BrandPackId, candidate.BrandPalette })
                .SingleOrDefaultAsync(cancellationToken);

            if (tenant is null)
            {
                return string.Empty;
            }

            var effective = resolver.Resolve(tenant.BrandPackId, tenant.BrandPalette, allowUserOverride: false, userPalette: null);
            if (effective.Palette is not { } palette)
            {
                return string.Empty;
            }

            return ThemeEmitter.PaletteCss(
                ThemeTokensReader.FromGroups(effective.ProductName, palette.Light.Groups, palette.Dark.Groups));
        }
        catch (Exception e) when (e is BrandPaletteInvalidException or InvalidOperationException)
        {
            // The pack store already refused a palette that cannot be painted at load; this is the belt to that
            // brace, because the reader of this page is the person in the system least able to do anything about
            // an error — they get the installation's colours instead (ADR 1103).
            return string.Empty;
        }
    }
}
