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
/// The ADMINISTRATIVE act of giving a tenant its runtime brand (ADR 1103, #2408): which pack, its default palette,
/// and whether the tenant's users may pick a palette themselves. A platform administrator's, and performed through
/// <c>alcifra</c> (ADR 0871) — assigning a showcase brand is a configuration act, never a hand-written request.
/// </summary>
/// <remarks>
/// The caller's own effective brand is <see cref="BrandController"/>; this resource only assigns. A pack the
/// installation does not carry is refused by name with the ids it does carry, because a silent fallback here would
/// leave a showcase wearing the wrong mark with nothing to read anywhere.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenants/{tenantId:guid}/brand")]
[Authorize]
public class TenantBrandController : ControllerBase
{
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministrator;
    private readonly AlcifraDbContext _db;
    private readonly IBrandPackStore _packs;
    private readonly IAuditRecorder _audit;

    public TenantBrandController(
        ICurrentPlatformAdministratorAccessor platformAdministrator,
        AlcifraDbContext db,
        IBrandPackStore packs,
        IAuditRecorder audit)
    {
        _platformAdministrator = platformAdministrator;
        _db = db;
        _packs = packs;
        _audit = audit;
    }

    public sealed class TenantBrandResource : HypermediaResource
    {
        public Guid TenantId { get; set; }

        public string? PackId { get; set; }

        public string? PaletteId { get; set; }

        public bool AllowUserPaletteOverride { get; set; }

        /// <summary>The packs this installation carries — the values <see cref="PackId"/> may take.</summary>
        public List<string> AvailablePackIds { get; set; } = [];
    }

    public sealed class AssignBrandRequest
    {
        /// <summary>The pack id, or null to return the tenant to the installation's own brand.</summary>
        public string? PackId { get; set; }

        /// <summary>The tenant's default palette within that pack, or null for the pack's own colours.</summary>
        public string? PaletteId { get; set; }

        public bool AllowUserPaletteOverride { get; set; }
    }

    [HttpGet]
    [HttpHead]
    public async Task<ActionResult<TenantBrandResource>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_platformAdministrator.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var tenant = await LoadAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return ToResource(tenant);
    }

    [HttpPut]
    public async Task<ActionResult<TenantBrandResource>> AssignAsync(
        Guid tenantId,
        [FromBody] AssignBrandRequest request,
        CancellationToken cancellationToken)
    {
        if (_platformAdministrator.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var pack = request.PackId is null ? null : _packs.Find(request.PackId);
        if (request.PackId is not null && pack is null)
        {
            throw new UnknownBrandPackException(_packs.AvailablePackIds);
        }

        if (pack is not null && request.PaletteId is not null && pack.FindPalette(request.PaletteId) is null)
        {
            throw new UnknownPaletteException();
        }

        var tenant = await LoadAsync(tenantId, cancellationToken, tracked: true);
        if (tenant is null)
        {
            return NotFound();
        }

        Concurrency.ConcurrencyHeaders.ApplyIfMatch(_db, Request, tenant);

        var before = $"{tenant.BrandPackId ?? "(installation brand)"}/{tenant.BrandPalette ?? "(pack default)"}/override {tenant.AllowUserPaletteOverride}";
        tenant.BrandPackId = request.PackId;
        tenant.BrandPalette = request.PaletteId;
        tenant.AllowUserPaletteOverride = request.AllowUserPaletteOverride;

        await _db.SaveChangesAsync(cancellationToken);

        var after = $"{tenant.BrandPackId ?? "(installation brand)"}/{tenant.BrandPalette ?? "(pack default)"}/override {tenant.AllowUserPaletteOverride}";
        if (after != before)
        {
            await _audit.RecordAsync(
                AuditActions.TenantBrandChanged,
                "Tenant",
                tenant.Id,
                tenant.Name,
                details: $"{before} -> {after}",
                tenantId: tenant.Id,
                cancellationToken: cancellationToken);
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return ToResource(tenant);
    }

    private async Task<Alcifra.Domain.Tenants.Tenant?> LoadAsync(Guid tenantId, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = _db.Tenants.AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(tenant => tenant.Id == tenantId, cancellationToken);
    }

    private TenantBrandResource ToResource(Alcifra.Domain.Tenants.Tenant tenant) => new()
    {
        TenantId = tenant.Id,
        PackId = tenant.BrandPackId,
        PaletteId = tenant.BrandPalette,
        AllowUserPaletteOverride = tenant.AllowUserPaletteOverride,
        AvailablePackIds = _packs.AvailablePackIds.ToList(),
        Links =
        [
            new Link("self", $"/api/tenants/{tenant.Id}/brand", "GET"),
            new Link("assign", $"/api/tenants/{tenant.Id}/brand", "PUT"),
        ],
    };
}
