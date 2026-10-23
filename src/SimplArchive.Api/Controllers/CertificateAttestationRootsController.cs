using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.Errors.Exceptions.Certificates;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A tenant's view of the installation's trusted attestation roots, and which of them it switches off for itself (#1790,
/// ADR 0963): a tenant administrator may narrow what the installation trusts, never widen it.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE <c>PUT</c> of the whole selection, guarded by the TENANT's tag</b> (owner decision 2026-10-22): the rows are
/// create-and-delete join rows with no token of their own, so the tenant's precondition is what keeps two administrators
/// from silently overwriting each other's selection, and the save moves it.
/// </para>
/// <para>
/// The same bar as the certificate settings beside it (ADR 0936): a module administrator, so the unattended setup account
/// that configures enrolment can configure this too. Offered only while a module that licenses enrolment is active.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/certificate-attestation-roots")]
[Authorize]
public class CertificateAttestationRootsController(
    SimplArchiveDbContext dbContext,
    CertificateEnrolment enrolment,
    ICurrentTenantAccessor tenantAccessor,
    Concurrency.TenantVerbs tenants,
    IAuditRecorder audit) : ControllerBase
{
    public class SelectionResource : HypermediaResource
    {
        public List<RootResource> Roots { get; set; } = [];
    }

    public class RootResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Thumbprint { get; set; } = string.Empty;

        /// <summary>Whether the installation trusts it; a tenant cannot switch on what the installation has not.</summary>
        public bool EnabledForInstallation { get; set; }

        /// <summary>Whether this tenant has switched it off.</summary>
        public bool DisabledHere { get; set; }

        /// <summary>Whether a key attested to it addresses its holder in this tenant: on for the installation, not off here.</summary>
        public bool Trusted { get; set; }
    }

    public class UpdateSelectionRequest
    {
        /// <summary>The roots this tenant switches off: the WHOLE list, replacing the previous one.</summary>
        public List<Guid> DisabledRootIds { get; set; } = [];
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var tenant = await AuthorizedTenantAsync(cancellationToken);
        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(await ResourceAsync(cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        Concurrency.ConcurrencyHeaders.EmitETag(Response, await AuthorizedTenantAsync(cancellationToken));
        return NoContent();
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpdateSelectionRequest request, CancellationToken cancellationToken)
    {
        var tenant = await AuthorizedTenantAsync(cancellationToken);
        Concurrency.ConcurrencyHeaders.RequireIfMatch(Request);

        var wanted = request.DisabledRootIds.Distinct().ToHashSet();
        var known = await dbContext.TrustedAttestationRoots.Where(r => wanted.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        if (wanted.FirstOrDefault(id => !known.ContainsKey(id)) is var unknown && unknown != Guid.Empty)
        {
            throw new CertificateAttestationRootUnknownException(unknown);
        }

        await tenants.MutateAsync(Request, tenant,
            apply: async () =>
            {
                var current = await dbContext.TenantDisabledAttestationRoots.ToListAsync(cancellationToken);
                dbContext.TenantDisabledAttestationRoots.RemoveRange(current.Where(c => !wanted.Contains(c.RootId)));
                dbContext.TenantDisabledAttestationRoots.AddRange(wanted.Where(id => current.All(c => c.RootId != id)).Select(id =>
                    new TenantDisabledAttestationRoot { TenantId = tenant.Id, RootId = id, CreatedAt = DateTimeOffset.UtcNow }));
            },
            afterCommit: () => audit.RecordAsync(AuditActions.CertificateAttestationRootsSelected, "Tenant", tenant.Id, tenant.Name,
                known.Count == 0 ? "no root switched off" : $"switched off: {string.Join(", ", known.Values.Order(StringComparer.Ordinal))}",
                cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(await ResourceAsync(cancellationToken));
    }

    private async Task<Domain.Tenants.Tenant> AuthorizedTenantAsync(CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        if (!await enrolment.CallerAdministersAsync(cancellationToken))
        {
            throw new CertificateSettingsNotYoursException();
        }

        return await dbContext.Tenants.SingleAsync(t => t.Id == tenantAccessor.TenantId, cancellationToken);
    }

    private async Task<SelectionResource> ResourceAsync(CancellationToken cancellationToken)
    {
        var disabled = await dbContext.TenantDisabledAttestationRoots.Select(d => d.RootId).ToListAsync(cancellationToken);
        var roots = await dbContext.TrustedAttestationRoots.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return new SelectionResource
        {
            Roots = [.. roots.Select(r => new RootResource
            {
                Id = r.Id,
                Name = r.Name,
                Thumbprint = r.Thumbprint,
                EnabledForInstallation = r.Enabled,
                DisabledHere = disabled.Contains(r.Id),
                Trusted = r.Enabled && !disabled.Contains(r.Id),
            })],
            Links = [new Link("self", "/api/certificate-attestation-roots", "GET")],
        };
    }
}
