using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.OfflineArchives;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The tenant's recovery certificate for offline exports (ADR 0960, #1695): every offline archive is also sealed to it.
/// Reached by the tenant settings' <c>settings-recovery-certificate</c> rel; tenant administrators only. One resource,
/// the method says the action (ADR 0719): <c>GET</c>/<c>HEAD</c> read it, <c>PUT</c> sets or replaces it with the
/// <c>If-Match</c> of the read that showed it (ADRs 0794, 0878). No <c>DELETE</c>: the setting is required, and
/// removing it would only make every export refused.
/// </summary>
/// <remarks>Its own controller rather than a section of the tenant settings, which are near the 1000-line limit.</remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenant-settings/recovery-certificate")]
[Authorize]
public class TenantRecoveryCertificateController(
    AlcifraDbContext dbContext,
    ICurrentTenantAccessor currentTenant,
    ICurrentUserAccessor currentUser,
    IUserSystemRightsResolver systemRights,
    Concurrency.TenantVerbs tenants,
    IAuditRecorder audit,
    TimeProvider clock) : ControllerBase
{
    private const string Self = "/api/tenant-settings/recovery-certificate";

    public class RecoveryCertificateResource : HypermediaResource
    {
        /// <summary>Whether one is set; without one every offline export is refused.</summary>
        public bool Configured { get; set; }

        /// <summary>The public certificate (PEM), or null.</summary>
        public string? CertificatePem { get; set; }

        /// <summary>Its SHA-256 thumbprint, as the archive's cover names it.</summary>
        public string? Thumbprint { get; set; }

        public string? Subject { get; set; }

        public DateTimeOffset? NotAfter { get; set; }
    }

    public class RecoveryCertificateRequest
    {
        /// <summary>The PUBLIC certificate alone (PEM); an upload with a private key is refused.</summary>
        public string CertificatePem { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (await AdministeredTenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(ToResource(tenant));
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        if (await AdministeredTenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return NoContent();
    }

    /// <summary>Sets or replaces it; validated first (ADR 0960), recorded in the audit trail by thumbprint.</summary>
    [HttpPut]
    public async Task<IActionResult> Put([FromBody] RecoveryCertificateRequest request, CancellationToken cancellationToken)
    {
        if (await AdministeredTenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        tenants.RequireIfMatch(Request);   // 428 without: tracked means the whole chain (ADR 0878)
        var accepted = RecoveryCertificate.Validate(request.CertificatePem ?? string.Empty, clock.GetUtcNow());
        var before = Describe(tenant);
        await tenants.MutateAsync(Request, tenant, apply: () =>
        {
            tenant.OfflineExportRecoveryCertificatePem = accepted.CertificatePem;
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);

        // The audit names the keys by thumbprint and holder; a certificate is public, but the line stays short.
        await audit.RecordAsync(AuditActions.TenantRecoveryCertificateSet, "Tenant", tenant.Id, tenant.Name,
            $"{before} → {accepted.Subject} ({accepted.Thumbprint})", cancellationToken: cancellationToken);

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(ToResource(tenant));
    }

    private async Task<Tenant?> AdministeredTenantAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !(await systemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin)
        {
            return null;
        }

        return await dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == currentTenant.TenantId, cancellationToken);
    }

    private static string Describe(Tenant tenant) =>
        tenant.OfflineExportRecoveryCertificatePem is { } pem ? Described(pem) : "none";

    private static string Described(string pem)
    {
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        return $"{certificate.Subject} ({certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)})";
    }

    private static RecoveryCertificateResource ToResource(Tenant tenant)
    {
        var resource = new RecoveryCertificateResource
        {
            Configured = tenant.OfflineExportRecoveryCertificatePem is not null,
            CertificatePem = tenant.OfflineExportRecoveryCertificatePem,
            Links = [new Link("self", Self, "GET")],
        };
        if (tenant.OfflineExportRecoveryCertificatePem is { } pem)
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            resource.Thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            resource.Subject = certificate.Subject;
            resource.NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        }

        return resource;
    }
}
