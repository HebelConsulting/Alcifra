using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Audience;
using SimplArchive.Api.Concurrency;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Audience;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The audience of encrypted content (ADR 0983): who is meant to decrypt a Password Store or a Sealed document, and who
/// may change that. Reached by the <c>audience</c> rel on the store and on the document.
/// </summary>
/// <remarks>
/// Asked of a folder or entry inside a store, it answers the STORE's audience: the store declares it for everything
/// beneath. A <c>PUT</c> replaces the whole declared list under the declaring document's <c>If-Match</c> and moves that
/// document's token, which is the concurrency guard the rows themselves do without (the owner's exemption, ADR 0878).
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/audience")]
[Authorize]
public class ContentAudienceController(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    DocumentVerbs documents,
    ContentAudience audience,
    IAuditRecorder audit,
    ICurrentUserAccessor currentUser,
    ICurrentServiceAccountAccessor currentServiceAccount,
    ICurrentTenantAccessor currentTenant,
    ILogger<ContentAudienceController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        var root = await VisibleRootAsync(documentId, cancellationToken);
        Response.Headers.ETag = Tag(root.ConcurrencyToken);
        return Ok(await BuildAsync(root, cancellationToken));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        var root = await VisibleRootAsync(documentId, cancellationToken);
        Response.Headers.ETag = Tag(root.ConcurrencyToken);
        return NoContent();
    }

    /// <summary>
    /// Replaces the declared audience. A manager may change anything that keeps a manager; a tenant administrator, while
    /// no manager is left, may only appoint one from among the readers. The client re-seals at once afterwards.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Put(Guid documentId, [FromBody] ContentAudienceWrite write, CancellationToken cancellationToken)
    {
        var root = await VisibleRootAsync(documentId, cancellationToken);
        if (root.IsPersonal)
        {
            throw new AudienceFixedInPersonalSpaceException();
        }

        documents.RequireIfMatch(Request);
        var wanted = Normalise(write);
        var stored = await dbContext.ContentAudienceMembers
            .Where(m => m.DocumentId == root.DocumentId)
            .ToListAsync(cancellationToken);

        var effective = await audience.EffectiveAsync(root, cancellationToken);
        var isManager = await audience.IsManagerAsync(root, currentUser.UserId, cancellationToken)
            || await SeedsUnmanagedAsync(root, effective, cancellationToken);
        var appointing = !isManager && !effective.Any(m => m.IsManager) && await access.IsTenantAdminAsync(cancellationToken);
        if (!isManager && !appointing)
        {
            throw new AudienceChangeRequiresManagerException();
        }

        var readers = (await audience.ReadersAsync(root, cancellationToken)).Select(r => r.Id).ToHashSet();
        var candidateGroups = (await audience.CandidatesAsync(root, cancellationToken)).Groups.Select(g => g.Id).ToHashSet();
        if (appointing)
        {
            RequireOnlyAppointments(stored, wanted, readers);
        }

        // A row already declared may stay as it is, even if its principal no longer reads (it is simply not in effect);
        // a NEW row must be a reader, the same judgement the candidates list states for the editor.
        foreach (var row in wanted)
        {
            var known = stored.Any(s => s.UserId == row.UserId && s.GroupId == row.GroupId);
            if (!known && (row.UserId is { } user ? !readers.Contains(user) : !candidateGroups.Contains(row.GroupId!.Value)))
            {
                throw new AudienceMemberNotAReaderException();
            }
        }

        if (!wanted.Any(r => r.IsManager && r.UserId is { } manager && readers.Contains(manager)))
        {
            throw new AudienceNeedsAManagerException();
        }

        // One commit: the rows and the declaring document's token, under the caller's precondition (ADR 0794). The
        // document's verb contract moves its token although only child rows changed (touchEntity), which is what makes
        // a second manager's stale list a 412.
        var tenantId = currentTenant.TenantId!.Value;
        var document = await dbContext.Documents.SingleAsync(d => d.Id == root.DocumentId, cancellationToken);
        var detail = $"{wanted.Count} member(s), {wanted.Count(r => r.IsManager)} manager(s)";
        await documents.MutateAsync(Request, document,
            apply: () =>
            {
                dbContext.ContentAudienceMembers.RemoveRange(stored);
                var now = DateTimeOffset.UtcNow;
                dbContext.ContentAudienceMembers.AddRange(wanted.Select(r => new ContentAudienceMember
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DocumentId = root.DocumentId,
                    UserId = r.UserId,
                    GroupId = r.GroupId,
                    IsManager = r.IsManager,
                    CreatedAt = now,
                }));
                return Task.CompletedTask;
            },
            afterCommit: () => audit.RecordAsync(
                appointing ? AuditActions.ContentAudienceManagerAppointed : AuditActions.ContentAudienceChanged,
                "Document", root.DocumentId, document.Name, detail, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
        logger.LogInformation("Audience of {Kind} {DocumentId} changed{Appointed}: {Detail}",
            root.Kind, root.DocumentId, appointing ? " by an administrator's appointment" : string.Empty, detail);

        audience.Forget(root);
        var updated = root with { ConcurrencyToken = document.ConcurrencyToken };
        Response.Headers.ETag = Tag(updated.ConcurrencyToken);
        return Ok(await BuildAsync(updated, cancellationToken));
    }

    /// <summary>
    /// The administrator's one act: every stored row kept as it was, except users made managers; added rows are new
    /// managers. Nothing removed, no group added, nobody demoted.
    /// </summary>
    private static void RequireOnlyAppointments(
        IReadOnlyList<ContentAudienceMember> stored, IReadOnlyList<ContentAudienceWriteMember> wanted, HashSet<Guid> readers)
    {
        foreach (var row in stored)
        {
            var kept = wanted.FirstOrDefault(w => w.UserId == row.UserId && w.GroupId == row.GroupId);
            if (kept is null || (row.IsManager && !kept.IsManager))
            {
                throw new AdministratorMayOnlyAppointException();
            }
        }

        foreach (var row in wanted)
        {
            var before = stored.FirstOrDefault(s => s.UserId == row.UserId && s.GroupId == row.GroupId);
            var appointed = row.IsManager && (before is null || !before.IsManager);
            if ((before is null && !row.IsManager) || (appointed && (row.UserId is not { } user || !readers.Contains(user))))
            {
                throw new AdministratorMayOnlyAppointException();
            }
        }
    }

    /// <summary>One row per principal, exactly one of user and group, a manager only ever a user.</summary>
    private static List<ContentAudienceWriteMember> Normalise(ContentAudienceWrite write)
    {
        var rows = new List<ContentAudienceWriteMember>();
        foreach (var row in write.Members)
        {
            if ((row.UserId is null) == (row.GroupId is null))
            {
                throw new AudienceMemberMalformedException();
            }

            if (row.IsManager && row.GroupId is not null)
            {
                throw new AudienceManagerMustBeAUserException();
            }

            if (rows.FirstOrDefault(r => r.UserId == row.UserId && r.GroupId == row.GroupId) is { } twice)
            {
                twice.IsManager |= row.IsManager;
                continue;
            }

            rows.Add(new ContentAudienceWriteMember { UserId = row.UserId, GroupId = row.GroupId, IsManager = row.IsManager });
        }

        return rows;
    }

    private async Task<ContentAudienceResource> BuildAsync(AudienceRoot root, CancellationToken cancellationToken)
    {
        var effective = await audience.EffectiveAsync(root, cancellationToken);
        var me = currentUser.UserId;
        var canManage = !root.IsPersonal
            && (await audience.IsManagerAsync(root, me, cancellationToken) || await SeedsUnmanagedAsync(root, effective, cancellationToken));
        var canAppoint = !root.IsPersonal && !canManage && !effective.Any(m => m.IsManager) && await access.IsTenantAdminAsync(cancellationToken);
        var effectiveIds = effective.Select(m => m.UserId).ToHashSet();

        var resource = new ContentAudienceResource
        {
            DocumentId = root.DocumentId,
            Kind = root.Kind == AudienceKind.PasswordStore ? "password-store" : "sealed-document",
            IsPersonal = root.IsPersonal,
            ETag = Tag(root.ConcurrencyToken),
            CanManage = canManage,
            CanAppointManager = canAppoint,
            IsMember = me is { } caller && effectiveIds.Contains(caller),
            Recipients =
            [
                .. effective.Select(m => new ContentAudienceRecipient
                {
                    UserId = m.UserId, DisplayName = m.DisplayName, IsManager = m.IsManager, CertificatePems = [.. m.CertificatePems],
                }),
            ],
        };

        foreach (var row in await audience.DeclaredAsync(root, cancellationToken))
        {
            resource.Members.Add(new ContentAudienceDeclaredMember
            {
                UserId = row.UserId,
                GroupId = row.GroupId,
                DisplayName = row.DisplayName,
                IsManager = row.IsManager,
                InEffect = row.UserId is { } user ? effectiveIds.Contains(user) : effective.Count > 0,
            });
        }

        if (canManage || canAppoint)
        {
            var (users, groups) = await audience.CandidatesAsync(root, cancellationToken);
            resource.Candidates.AddRange(users.Select(u => new ContentAudienceCandidate { UserId = u.Id, DisplayName = u.DisplayName }));
            // An administrator appoints a PERSON; offering groups would invite a refusal.
            if (canManage)
            {
                resource.Candidates.AddRange(groups.Select(g => new ContentAudienceCandidate { GroupId = g.Id, DisplayName = g.Name }));
            }
        }

        // One rel for the resource (ADR 0719): `self` serves GET and PUT, and the write is said by the capability flags
        // CanManage and CanAppointManager, not by a second rel of the same name.
        resource.Links.Add(new Link("self", $"/api/documents/{root.DocumentId}/audience", "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{root.DocumentId}", "GET"));

        return resource;
    }

    /// <summary>
    /// UNATTENDED SEEDING (owner decision 2026-10-26): a service account — saconsole's seeding, which has no person to
    /// name (ADR 0966) — writes the whole audience of an item no person manages yet, given the right to manage its
    /// permissions. It is never a member or a manager itself; once the audience names a manager, only managers change it.
    /// </summary>
    private async Task<bool> SeedsUnmanagedAsync(AudienceRoot root, IReadOnlyList<AudienceMember> effective, CancellationToken cancellationToken) =>
        currentServiceAccount.ServiceAccountId is not null
        && !effective.Any(m => m.IsManager)
        && (await access.GetCallerRightsAsync(root.DocumentId, cancellationToken)).CanManagePermissions;

    private async Task<AudienceRoot> VisibleRootAsync(Guid documentId, CancellationToken cancellationToken) =>
        await audience.RootOfAsync(documentId, cancellationToken) is { } root
        && (await access.GetCallerRightsAsync(root.DocumentId, cancellationToken)).CanSee
            ? root
            : throw new NoAudienceException();

    private static string Tag(Guid token) => $"\"{token}\"";
}
