using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.Branding;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Application.Branding;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The brand the CALLER wears after signing in (ADR 1103, #2408): their tenant's runtime brand pack — product name,
/// the palettes to choose from, the theme tokens and the asset files — or the installation's own brand when the
/// tenant has no pack.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated on purpose, and that is the legal rule made structural: a tenant brand exists only behind a
/// token, so no anonymous page — sign-in, reset, the external-link landing — can fetch one, whatever a renderer
/// later does. The anonymous surfaces keep the installation's compiled brand, which is what the crawler proves.
/// </para>
/// <para>
/// One resource per caller rather than a collection addressed by pack id: a caller may only ever see the brand of
/// their own tenant, so an id in the URL would only invite probing.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/brand")]
[Authorize]
public class BrandController : ControllerBase
{
    private readonly ICurrentTenantAccessor _tenant;
    private readonly ICurrentUserAccessor _user;
    private readonly AlcifraDbContext _db;
    private readonly TenantBrandResolver _resolver;
    private readonly IBrandPackStore _packs;

    public BrandController(
        ICurrentTenantAccessor tenant,
        ICurrentUserAccessor user,
        AlcifraDbContext db,
        TenantBrandResolver resolver,
        IBrandPackStore packs)
    {
        _tenant = tenant;
        _user = user;
        _db = db;
        _resolver = resolver;
        _packs = packs;
    }

    public sealed class BrandPaletteResource
    {
        public string? Id { get; set; }

        public Dictionary<string, string> Name { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BrandResource : HypermediaResource
    {
        /// <summary>The tenant's pack id — null when the caller wears the installation's own brand.</summary>
        public string? PackId { get; set; }

        /// <summary>The product name to show wherever the product names itself.</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>The palette in force, after the tenant default and the user's override.</summary>
        public string? PaletteId { get; set; }

        /// <summary>True when the caller may choose another palette in self-service.</summary>
        public bool CanChoosePalette { get; set; }

        /// <summary>The palettes this brand carries (the self-service chooser's contents).</summary>
        public List<BrandPaletteResource> Palettes { get; set; } = [];

        /// <summary>The palette's tokens, exactly as the brand file writes them; null for the installation brand.</summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>>? Theme { get; set; }

        /// <summary>The pack's version — the clients cache assets and tokens against it.</summary>
        public string? Version { get; set; }
    }

    [HttpGet]
    [HttpHead]
    public async Task<ActionResult<BrandResource>> GetAsync(CancellationToken cancellationToken)
    {
        var effective = await ResolveAsync(cancellationToken);
        var resource = new BrandResource
        {
            PackId = effective.PackId,
            ProductName = effective.ProductName,
            PaletteId = effective.PaletteId,
            CanChoosePalette = effective.CanChoosePalette,
            Palettes = effective.Palettes
                .Select(palette => new BrandPaletteResource { Id = palette.Id, Name = new Dictionary<string, string>(palette.Names, StringComparer.OrdinalIgnoreCase) })
                .ToList(),
            Version = effective.Version,
        };

        if (effective.Palette is { } chosen)
        {
            resource.Theme = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["light"] = Groups(chosen.Light),
                ["dark"] = Groups(chosen.Dark),
            };
        }

        if (effective.PackId is not null)
        {
            foreach (var (name, _) in effective.Assets)
            {
                resource.Links.Add(new Link("asset", $"/api/brand/assets/{name}", "GET"));
            }
        }

        if (effective.PackId is not null && effective.CanChoosePalette)
        {
            // The self-service chooser writes through the caller's own account (PUT the palette). The rel is only
            // offered when a choice exists, so a client never shows an affordance that would do nothing.
            resource.Links.Add(new Link("select-palette", "/api/brand/palette", "PUT"));
        }

        if (resource.Version is { Length: > 0 } version)
        {
            Response.Headers.ETag = $"\"{version}\"";
        }

        return resource;
    }

    /// <summary>
    /// One of the current brand's DECLARED asset files. The name must be one the pack's manifest lists, so no caller
    /// can address a path the pack did not publish.
    /// </summary>
    [HttpGet("assets/{name}")]
    [HttpHead("assets/{name}")]
    public async Task<IActionResult> GetAssetAsync(string name, CancellationToken cancellationToken)
    {
        var effective = await ResolveAsync(cancellationToken);
        if (effective.PackId is null || !effective.Assets.ContainsKey(name))
        {
            return NotFound();
        }

        var asset = _packs.Open(effective.PackId, name);
        if (asset is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = $"\"{effective.Version}\"";
        Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        return File(asset.Content, asset.ContentType);
    }

    public sealed class PaletteSelection
    {
        /// <summary>The variant id to store for the caller — null clears the override (the tenant default applies).</summary>
        public string? PaletteId { get; set; }
    }

    /// <summary>The caller's own palette within the current brand — self-service, only while the tenant allows it.</summary>
    [HttpPut("palette")]
    public async Task<IActionResult> SelectPaletteAsync([FromBody] PaletteSelection selection, CancellationToken cancellationToken)
    {
        var effective = await ResolveAsync(cancellationToken);
        if (effective.PackId is null)
        {
            throw new NoBrandPackException();
        }

        if (selection.PaletteId is not null && effective.Palettes.All(palette => palette.Id != selection.PaletteId))
        {
            throw new UnknownPaletteException();
        }

        if (_user.UserId is not { } userId)
        {
            // A service account has no self to store a preference on, and the tenant's default is what it wears.
            throw new NoUserProfileException();
        }

        var user = await _db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        user.BrandPalette = selection.PaletteId;
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private async Task<EffectiveBrand> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_tenant.TenantId is not { } tenantId)
        {
            return EffectiveBrand.Installation(_resolver.Resolve(null, null, false, null).ProductName);
        }

        var tenant = await _db.Tenants
            .AsNoTracking()
            .Where(candidate => candidate.Id == tenantId)
            .Select(candidate => new { candidate.BrandPackId, candidate.BrandPalette, candidate.AllowUserPaletteOverride })
            .SingleOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return EffectiveBrand.Installation(_resolver.Resolve(null, null, false, null).ProductName);
        }

        string? userPalette = null;
        if (_user.UserId is { } userId)
        {
            userPalette = await _db.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId)
                .Select(candidate => candidate.BrandPalette)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return _resolver.Resolve(tenant.BrandPackId, tenant.BrandPalette, tenant.AllowUserPaletteOverride, userPalette);
    }

    private static Dictionary<string, Dictionary<string, string>> Groups(BrandTheme theme) =>
        theme.Groups.ToDictionary(
            group => group.Key,
            group => new Dictionary<string, string>(group.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
}
