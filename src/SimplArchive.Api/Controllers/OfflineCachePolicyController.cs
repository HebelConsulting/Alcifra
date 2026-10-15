using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Tenant;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// What the tenant allows a phone's offline cache (#1742, owner decisions 2026-10-13; SimplArchiveMobile #5). One rel,
/// <c>offlineCachePolicy</c> on <c>/me</c>, the method says which action (ADR 0719): every user READS it, because the
/// phone shows only the allowed choices; a tenant administrator WRITES it with <c>PUT</c> and <c>If-Match</c>, which
/// the <c>canEdit</c> flag announces. Its own controller rather than a section of the tenant settings, which are
/// administrator-only and near the 1000-line limit.
/// </summary>
/// <remarks>The policy binds our apps, which honour what the server advertises; the server cannot enforce what a phone
/// stores, and the manual says so rather than promising it.</remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/offline-cache-policy")]
[Authorize]
public class OfflineCachePolicyController(
    SimplArchiveDbContext dbContext,
    ICurrentTenantAccessor currentTenant,
    ICurrentUserAccessor currentUser,
    IUserSystemRightsResolver systemRights,
    Concurrency.TenantVerbs tenants,
    IAuditRecorder audit,
    Infrastructure.Modules.ModuleOfflineExportPolicy modulePolicy) : ControllerBase
{
    public class OfflineCachePolicyResource : HypermediaResource
    {
        public bool Allowed { get; set; }

        public bool Notifications { get; set; }

        public bool OpenedDocuments { get; set; }

        public bool ChosenFolders { get; set; }

        /// <summary><c>UserChooses</c>, <c>Session</c> or <c>Document</c>.</summary>
        public string UnlockMode { get; set; } = string.Empty;

        /// <summary><c>UserChooses</c>, <c>KeepUpdated</c> or <c>RemoveWhenNewer</c>.</summary>
        public string RefreshMode { get; set; } = string.Empty;

        public int MaxUnlockedMinutes { get; set; }

        /// <summary>
        /// Whether a phone may hold the PIN of the user's hardware key for a session (ADR 0931): the Encryption Module's
        /// per-tenant answer, false where no module answers. Every sealed object is still decrypted by the key; this only
        /// spares retyping the PIN. It caps <see cref="UnlockMode"/>: false means a PIN for every decryption.
        /// </summary>
        public bool PinOncePerSession { get; set; }

        /// <summary>Whether the caller may change it (a tenant administrator).</summary>
        public bool CanEdit { get; set; }
    }

    public class OfflineCachePolicyRequest
    {
        public bool Allowed { get; set; }

        public bool Notifications { get; set; }

        public bool OpenedDocuments { get; set; }

        public bool ChosenFolders { get; set; }

        public string UnlockMode { get; set; } = string.Empty;

        public string RefreshMode { get; set; } = string.Empty;

        public int MaxUnlockedMinutes { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || await TenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(ToResource(tenant, await IsTenantAdminAsync(cancellationToken), await PinOncePerSessionAsync(cancellationToken)));
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || await TenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return NoContent();
    }

    /// <summary>The whole policy, as one PUT with the tag of the read that filled the form (ADRs 0794, 0872).</summary>
    [HttpPut]
    public async Task<IActionResult> Put([FromBody] OfflineCachePolicyRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken) || await TenantAsync(cancellationToken) is not { } tenant)
        {
            return Forbid();
        }

        tenants.RequireIfMatch(Request);   // 428 without: tracked means the whole chain (ADR 0878)
        if (!Enum.TryParse<OfflineCacheUnlockMode>(request.UnlockMode, ignoreCase: false, out var unlock) || !Enum.IsDefined(unlock)
            || !Enum.TryParse<OfflineCacheRefreshMode>(request.RefreshMode, ignoreCase: false, out var refresh) || !Enum.IsDefined(refresh)
            || request.MaxUnlockedMinutes is < 1 or > 1440)
        {
            throw new InvalidOfflineCachePolicyException();
        }

        var before = Describe(tenant);
        await tenants.MutateAsync(Request, tenant, apply: () =>
        {
            tenant.OfflineCacheAllowed = request.Allowed;
            tenant.OfflineCacheNotifications = request.Notifications;
            tenant.OfflineCacheOpenedDocuments = request.OpenedDocuments;
            tenant.OfflineCacheChosenFolders = request.ChosenFolders;
            tenant.OfflineCacheUnlockMode = unlock;
            tenant.OfflineCacheRefreshMode = refresh;
            tenant.OfflineCacheMaxUnlockedMinutes = request.MaxUnlockedMinutes;
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);

        if (Describe(tenant) is var after && after != before)
        {
            await audit.RecordAsync(AuditActions.TenantOfflineCachePolicyUpdated, "Tenant", tenant.Id, tenant.Name, $"{before} → {after}",
                cancellationToken: cancellationToken);
        }

        Concurrency.ConcurrencyHeaders.EmitETag(Response, tenant);
        return Ok(ToResource(tenant, canEdit: true, await PinOncePerSessionAsync(cancellationToken)));
    }

    private Task<Tenant?> TenantAsync(CancellationToken cancellationToken) =>
        dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == currentTenant.TenantId, cancellationToken);

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId && (await systemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;

    // The audit names what changed, in the policy's own words; nothing here is secret.
    private static string Describe(Tenant t) =>
        $"allowed={t.OfflineCacheAllowed}, notifications={t.OfflineCacheNotifications}, opened={t.OfflineCacheOpenedDocuments}, "
        + $"folders={t.OfflineCacheChosenFolders}, unlock={t.OfflineCacheUnlockMode}, refresh={t.OfflineCacheRefreshMode}, "
        + $"maxUnlockedMinutes={t.OfflineCacheMaxUnlockedMinutes}";

    // The module's answer (ABI 1.16's ContentOncePerSession, its meaning per ADR 0931); no module answers "no".
    private async Task<bool> PinOncePerSessionAsync(CancellationToken cancellationToken) =>
        (await modulePolicy.ForTenantAsync(cancellationToken)).ContentOncePerSession;

    private static OfflineCachePolicyResource ToResource(Tenant t, bool canEdit, bool pinOncePerSession) => new()
    {
        PinOncePerSession = pinOncePerSession,
        Allowed = t.OfflineCacheAllowed,
        Notifications = t.OfflineCacheNotifications,
        OpenedDocuments = t.OfflineCacheOpenedDocuments,
        ChosenFolders = t.OfflineCacheChosenFolders,
        UnlockMode = t.OfflineCacheUnlockMode.ToString(),
        RefreshMode = t.OfflineCacheRefreshMode.ToString(),
        MaxUnlockedMinutes = t.OfflineCacheMaxUnlockedMinutes,
        CanEdit = canEdit,
        Links = [new Link("self", "/api/offline-cache-policy", "GET")],
    };
}
