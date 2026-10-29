using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Certificates;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Certificates;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// Certificate enrolment's two tenant settings (ADR 0936): whether users enrol their own reader certificate, and the
/// repository or folder the "Reader certificates" folder is created under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Their own resource, not a tenant-settings section, because of WHO sets them.</b> They were the Encryption Module's
/// settings, which the module-administration right sets: a tenant administrator, or a service account granted
/// <c>CanManageModules</c> (the kiosk's unattended setup account is one). The tenant settings are a tenant
/// administrator's alone and refuse every service account, so moving the values there would have taken the setup's
/// ability away. Stored as the tenant's own columns all the same.
/// </para>
/// <para>
/// Offered only while a module that licenses enrolment is active. <c>PUT</c> carries the whole intended value and the
/// tenant's tag as <c>If-Match</c>.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/certificate-settings")]
[Authorize]
public class CertificateSettingsController(
    AlcifraDbContext dbContext,
    CertificateEnrolment enrolment,
    ICurrentTenantAccessor tenantAccessor,
    Concurrency.TenantVerbs tenants,
    IAuditRecorder audit) : ControllerBase
{
    public class CertificateSettingsResource : HypermediaResource
    {
        public bool SelfServiceEnabled { get; set; }
        public Guid? ParentFolderId { get; set; }

        // Its name, so a settings form shows the folder rather than an id; null when none is set or it is gone.
        public string? ParentFolderName { get; set; }

        // Whether certificates whose key is not attested to a trusted root are accepted (ADR 0963); default off.
        public bool UnattestedHardwareAllowed { get; set; }
    }

    public class UpdateCertificateSettingsRequest
    {
        public bool SelfServiceEnabled { get; set; }
        public Guid? ParentFolderId { get; set; }
        public bool UnattestedHardwareAllowed { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var tenant = await AuthorizedTenantAsync(cancellationToken);
        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(await ResourceAsync(tenant, cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        Concurrency.ConcurrencyHeaders.EmitETag(Response, await AuthorizedTenantAsync(cancellationToken));
        return NoContent();
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpdateCertificateSettingsRequest request, CancellationToken cancellationToken)
    {
        var tenant = await AuthorizedTenantAsync(cancellationToken);
        Concurrency.ConcurrencyHeaders.RequireIfMatch(Request);

        // The parent must be a live document of this tenant (the tenant filter scopes the lookup); a dangling id would
        // only surface later, as enrolment's "nowhere to file".
        if (request.ParentFolderId is { } parentId && !await dbContext.Documents.AnyAsync(d => d.Id == parentId, cancellationToken))
        {
            throw new CertificateParentFolderNotFoundException(parentId);
        }

        var wasUnattestedAllowed = tenant.CertificateUnattestedHardwareAllowed;
        var before = $"self-service {OnOff(tenant.CertificateSelfServiceEnabled)}, parent {tenant.CertificateParentFolderId?.ToString() ?? "none"}";
        await tenants.MutateAsync(Request, tenant,
            apply: () =>
            {
                tenant.CertificateSelfServiceEnabled = request.SelfServiceEnabled;
                tenant.CertificateParentFolderId = request.ParentFolderId;
                tenant.CertificateUnattestedHardwareAllowed = request.UnattestedHardwareAllowed;
                return Task.CompletedTask;
            },
            afterCommit: async () =>
            {
                await audit.RecordAsync(AuditActions.TenantSettingsCertificatesUpdated, "Tenant", tenant.Id, tenant.Name,
                    $"{before} → self-service {OnOff(tenant.CertificateSelfServiceEnabled)}, parent {tenant.CertificateParentFolderId?.ToString() ?? "none"}",
                    cancellationToken: cancellationToken);

                // Its own event (ADR 0963): admitting keys that may be software keys is a security decision an auditor looks for.
                if (wasUnattestedAllowed != tenant.CertificateUnattestedHardwareAllowed)
                {
                    await audit.RecordAsync(AuditActions.CertificateUnattestedHardwareChanged, "Tenant", tenant.Id, tenant.Name,
                        $"hardware without attestation {(tenant.CertificateUnattestedHardwareAllowed ? "allowed" : "refused")}", cancellationToken: cancellationToken);
                }
            },
            cancellationToken: cancellationToken);

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(await ResourceAsync(tenant, cancellationToken));
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

    private static string OnOff(bool on) => on ? "on" : "off";

    private async Task<CertificateSettingsResource> ResourceAsync(Domain.Tenants.Tenant tenant, CancellationToken cancellationToken) => new()
    {
        SelfServiceEnabled = tenant.CertificateSelfServiceEnabled,
        ParentFolderId = tenant.CertificateParentFolderId,
        ParentFolderName = tenant.CertificateParentFolderId is { } id
            ? await dbContext.Documents.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken)
            : null,
        UnattestedHardwareAllowed = tenant.CertificateUnattestedHardwareAllowed,
        Links = [new Link("self", "/api/certificate-settings", "GET")],
    };
}
