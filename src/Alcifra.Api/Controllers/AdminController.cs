using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Notifications;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The tenant-admin administration surface: its index, which the API root's <c>admin</c> rel reaches, and the take-over
/// of a personal space (ADR 0672). The listing of every user's personal space that backed the clients' synthetic
/// "Administration → Users" branch (ADR 0377) retired with ADR 1066: the spaces are referenced from the real
/// <c>Users</c> folder, read through the ordinary tree, and that listing records the access and carries
/// <c>take-over</c> on each row.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly AlcifraDbContext _dbContext;
    private readonly Concurrency.AclEntryVerbs _aclEntries;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditRecorder _auditRecorder;
    private readonly INotificationService _notifications;

    public AdminController(
        AlcifraDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditRecorder auditRecorder,
        INotificationService notifications,
        Concurrency.AclEntryVerbs aclEntries)
    {
        _dbContext = dbContext;
        _aclEntries = aclEntries;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _auditRecorder = auditRecorder;
        _notifications = notifications;
    }

    // The lifecycle right, not the browsing one (ADR 0672): taking over a departed person's space is the same
    // act as deactivating them, and it is audited and announced rather than silent.
    private async Task<bool> CanManageUsersAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageUsers;

    // The administration index. The API root advertises an `admin` rel pointing here, and a client follows what it
    // is offered (ADR 0543), so the index answers rather than dangling (#416). What it used to advertise — the
    // listing of every personal space — retired with ADR 1066: the spaces are reached through the Administration
    // folder in the ordinary tree, whose Users listing carries `take-over` on each row.
    [HttpGet]
    public IActionResult Index() =>
        Ok(new AdminIndexResource { Links = [new Link("self", "/api/admin", "GET")] });

    [HttpHead]
    public IActionResult IndexHead() => NoContent();

    public class AdminIndexResource : HypermediaResource;

    /// <summary>
    /// Grants the calling administrator full rights on one user's personal space (ADR 0672).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Privacy here means no SILENT access, not no access. Offboarding and GDPR both need a way into a space
    /// nobody else can reach, and the alternatives were worse: deferring it ships a lockout, and letting
    /// deactivation lift exclusivity makes access appear at the exact moment the owner can no longer see it.
    /// So the way in is explicit, audited, and announced to the owner.
    /// </para>
    /// <para>
    /// Gated on <b>CanManageUsers</b>, not on CanAccessWithoutGrant: taking over a departed person's space is a
    /// user-lifecycle act, the same right that deactivates them. An administrator who gave up their own x-ray
    /// may still do this, and that is not a hole — the act is recorded and the owner is told, which is exactly
    /// the property the privacy rule protects.
    /// </para>
    /// <para>
    /// A verb in the path, justified against #694 the way <c>/restore</c> is: this is a genuine transition, not
    /// a create, replace or delete of anything.
    /// </para>
    /// </remarks>
    [HttpPost("personal-repositories/{userId:guid}/take-over")]
    public async Task<IActionResult> TakeOverPersonalRepository(Guid userId, CancellationToken cancellationToken)
    {
        if (!await CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (_currentUserAccessor.UserId is not { } actorId)
        {
            return Forbid();
        }

        var root = await _dbContext.Documents
            .SingleOrDefaultAsync(d => d.PersonalOfUserId == userId, cancellationToken);

        if (root is null)
        {
            return NotFound();
        }

        var owner = await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.IsActive })
            .SingleAsync(cancellationToken);

        // Get-or-update rather than insert: AclEntry carries a partial unique index per principal per document,
        // so a second take-over of the same space would violate it. Idempotent is also the honest behaviour —
        // asking twice for access you already hold is not an error.
        var grant = await _dbContext.AclEntries
            .SingleOrDefaultAsync(a => a.DocumentId == root.Id && a.UserId == actorId, cancellationToken);

        if (grant is null)
        {
            grant = new AclEntry
            {
                Id = Guid.NewGuid(),
                TenantId = root.TenantId,
                DocumentId = root.Id,
                UserId = actorId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.AclEntries.Add(grant);
        }

        grant.CanSee = true;
        grant.CanReadContent = true;
        grant.CanEditContent = true;
        grant.CanEditIndexData = true;
        grant.CanDelete = true;
        grant.CanCreateSubItems = true;
        grant.CanManagePermissions = true;
        grant.CanMove = true;
        grant.CanAnnotate = true;

        // Through the entry's verb contract (ADR 0795). This is a platform administrator taking over somebody's
        // personal space, and it is create-OR-UPDATE: an existing grant is rewritten, so two administrators
        // acting on the same space could silently overwrite each other's decision. touchEntity is FALSE because
        // every path above writes the entry's own columns, and a grant being ADDED has no prior version.
        await _aclEntries.MutateAsync(
            Request, grant, apply: () => Task.CompletedTask, touchEntity: false, cancellationToken: cancellationToken);

        await _auditRecorder.RecordAsync(
            AuditActions.PersonalSpaceTakenOver,
            "Document",
            root.Id,
            root.Name,
            $"Took over the personal space of '{owner.DisplayName}'.",
            cancellationToken: cancellationToken);

        // Told, not merely recorded — and only while there is somebody to tell. A deactivated owner cannot read
        // notifications, so for them the audit log is the record, which is what offboarding needs anyway.
        if (owner.IsActive)
        {
            await _notifications.NotifyAsync(
                userId,
                NotificationType.PersonalSpaceTakenOver,
                "An administrator took over your personal space",
                $"Your personal space '{root.Name}' was taken over by an administrator, who now has full "
                + "access to it.",
                root.Id,
                cancellationToken);
        }

        return Ok(new TakeOverResource
        {
            RepositoryId = root.Id,
            Links =
            [
                new Link("document", $"/api/documents/{root.Id}", "GET"),
                new Link("children", $"/api/documents/{root.Id}/children", "GET"),
                // The grant is an ordinary ACL entry and is revoked like one, so the way to undo this is the
                // permissions dialog the caller already knows (ADR 0672).
                new Link("acl-entries", $"/api/documents/{root.Id}/acl-entries", "GET"),
            ],
        });
    }

    public class TakeOverResource : HypermediaResource
    {
        public Guid RepositoryId { get; set; }
    }
}
