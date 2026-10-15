using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Certificates;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

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
    SimplArchiveDbContext dbContext,
    CertificateEnrolment enrolment,
    ICurrentTenantAccessor tenantAccessor,
    Concurrency.TenantVerbs tenants,
    IAuditRecorder audit) : ControllerBase
{
    public class CertificateSettingsResource : HypermediaResource
    {
        public bool SelfServiceEnabled { get; set; }
        public Guid? ParentFolderId { get; set; }
    }

    public class UpdateCertificateSettingsRequest
    {
        public bool SelfServiceEnabled { get; set; }
        public Guid? ParentFolderId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var tenant = await AuthorizedTenantAsync(cancellationToken);
        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(Resource(tenant));
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

        var before = $"self-service {OnOff(tenant.CertificateSelfServiceEnabled)}, parent {tenant.CertificateParentFolderId?.ToString() ?? "none"}";
        await tenants.MutateAsync(Request, tenant,
            apply: () =>
            {
                tenant.CertificateSelfServiceEnabled = request.SelfServiceEnabled;
                tenant.CertificateParentFolderId = request.ParentFolderId;
                return Task.CompletedTask;
            },
            afterCommit: () => audit.RecordAsync(AuditActions.TenantSettingsCertificatesUpdated, "Tenant", tenant.Id, tenant.Name,
                $"{before} → self-service {OnOff(tenant.CertificateSelfServiceEnabled)}, parent {tenant.CertificateParentFolderId?.ToString() ?? "none"}",
                cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(Resource(tenant));
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

    private static CertificateSettingsResource Resource(Domain.Tenants.Tenant tenant) => new()
    {
        SelfServiceEnabled = tenant.CertificateSelfServiceEnabled,
        ParentFolderId = tenant.CertificateParentFolderId,
        Links = [new Link("self", "/api/certificate-settings", "GET")],
    };
}
