using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.PasswordStores;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Audience;

/// <summary>Which sealed mask declares an audience (ADR 0983).</summary>
public enum AudienceKind
{
    PasswordStore,
    SealedDocument,
}

/// <summary>
/// The document that DECLARES an audience: a Password Store (for every folder and entry beneath it) or a Sealed
/// document. <see cref="PersonalOwnerId"/> is set inside a personal space, whose owner is the whole audience.
/// </summary>
public sealed record AudienceRoot(Guid DocumentId, AudienceKind Kind, Guid? PersonalOwnerId, Guid ConcurrencyToken)
{
    public bool IsPersonal => PersonalOwnerId is not null;
}

/// <summary>One person in the EFFECTIVE audience, with the attested certificates their copies are sealed to (possibly none).</summary>
public sealed record AudienceMember(Guid UserId, string DisplayName, bool IsManager, IReadOnlyList<string> CertificatePems);

/// <summary>One row of the DECLARED audience as an editor shows it: a user or a group, by name.</summary>
public sealed record DeclaredAudienceMember(Guid? UserId, Guid? GroupId, string DisplayName, bool IsManager);

/// <summary>
/// Who is MEANT to decrypt a sealed item, and who actually may (ADR 0983): the declared audience, intersected with the
/// readers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Effective = declared ∩ readers.</b> The declared members are users, and the members of declared groups
/// (transitively, membership flows down); of those, only ACTIVE users with an explicit grant of read on the declaring
/// document count — <see cref="IEffectiveRightsCalculator.GetGrantedRightsAsync"/>, which leaves out the tenant
/// administrator's bypass, so the bypass never makes anyone a member. Losing read takes a member out at once without
/// touching the declaration: the two lists are kept agreeing by computing their intersection, never by writing one
/// from the other.
/// </para>
/// <para>
/// <b>A personal space is its owner's</b>: no rows, the owner is the only member and manager.
/// </para>
/// <para>
/// <b>Only an ANSWER counts</b> for certificates (ADR 0963): <see cref="ReaderCertificateSet"/> addresses attested keys
/// only when a module answered; its fall-back to the user's own column would address a key nobody proved lives on a
/// token, so a member without an answer is a member without certificates.
/// </para>
/// <para>Memoised for the request (registered scoped): a listing asks once per declaring document, not per row.</para>
/// </remarks>
public sealed class ContentAudience(
    SimplArchiveDbContext dbContext,
    IEffectiveRightsCalculator rights,
    ReaderCertificateSet readerCertificates,
    PasswordStoreTree tree,
    ICurrentUserAccessor currentUser,
    ILogger<ContentAudience> logger)
{
    private readonly Dictionary<Guid, IReadOnlyList<AudienceMember>> _effective = [];
    private readonly Dictionary<Guid, AudienceRoot?> _roots = [];

    /// <summary>The kind of audience a mask declares, or null for a mask that declares none.</summary>
    public static AudienceKind? DeclaredBy(Guid? maskId) =>
        maskId == WellKnownMaskIds.PasswordStore ? AudienceKind.PasswordStore
        : maskId == WellKnownMaskIds.SealedDocument ? AudienceKind.SealedDocument
        : null;

    /// <summary>
    /// The document whose audience decides who decrypts <paramref name="documentId"/>: itself for a store or a Sealed
    /// document, the store for a folder or entry inside one; null for anything sealed to no audience.
    /// </summary>
    public async Task<AudienceRoot?> RootOfAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_roots.TryGetValue(documentId, out var known))
        {
            return known;
        }

        var row = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new
            {
                d.Id,
                d.PersonalRootOwnerId,
                d.ConcurrencyToken,
                MaskId = dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        AudienceRoot? root = null;
        if (row is not null && DeclaredBy(row.MaskId) is { } kind)
        {
            root = new AudienceRoot(row.Id, kind, row.PersonalRootOwnerId, row.ConcurrencyToken);
        }
        else if (row is not null && PasswordStoreTree.KindOf(row.MaskId) is not null
            && await tree.FindAsync(documentId, cancellationToken) is { } item && item.StoreId != documentId)
        {
            root = await RootOfAsync(item.StoreId, cancellationToken);
        }

        _roots[documentId] = root;
        return root;
    }

    /// <summary>The effective audience of <paramref name="root"/>: declared members who are active readers, by name.</summary>
    public async Task<IReadOnlyList<AudienceMember>> EffectiveAsync(AudienceRoot root, CancellationToken cancellationToken)
    {
        if (_effective.TryGetValue(root.DocumentId, out var known))
        {
            return known;
        }

        // Who is declared: the owner of a personal space, else the rows — users directly, groups by their members.
        var managers = new HashSet<Guid>();
        var declared = new HashSet<Guid>();
        if (root.PersonalOwnerId is { } owner)
        {
            declared.Add(owner);
            managers.Add(owner);
        }
        else
        {
            var rows = await dbContext.ContentAudienceMembers.AsNoTracking()
                .Where(m => m.DocumentId == root.DocumentId)
                .Select(m => new { m.UserId, m.GroupId, m.IsManager })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.UserId is { } userId)
                {
                    declared.Add(userId);
                    if (row.IsManager)
                    {
                        managers.Add(userId);
                    }
                }
                else if (row.GroupId is { } groupId)
                {
                    declared.UnionWith(await GroupMembershipExpansion.GetUsersEffectivelyInGroupAsync(dbContext, groupId, cancellationToken));
                }
            }
        }

        var users = await dbContext.Users
            .Where(u => u.IsActive && declared.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        var members = new List<AudienceMember>();
        foreach (var user in users)
        {
            // A personal space's owner reads it by the space's own grant; everybody else must hold a grant of read.
            if (root.PersonalOwnerId != user.Id
                && !(await rights.GetGrantedRightsAsync(user.Id, root.DocumentId, cancellationToken)).CanReadContent)
            {
                continue;
            }

            members.Add(new AudienceMember(user.Id, user.DisplayName, managers.Contains(user.Id), await CertificatesOfAsync(user.Id, root, cancellationToken)));
        }

        logger.LogDebug("Audience of {Kind} {DocumentId}: {Declared} declared, {Members} effective, {Managers} manager(s)",
            root.Kind, root.DocumentId, declared.Count, members.Count, members.Count(m => m.IsManager));
        _effective[root.DocumentId] = members;
        return members;
    }

    /// <summary>Whether <paramref name="userId"/> is in the effective audience of <paramref name="root"/>.</summary>
    public async Task<bool> IsMemberAsync(AudienceRoot root, Guid? userId, CancellationToken cancellationToken) =>
        userId is { } id && (await EffectiveAsync(root, cancellationToken)).Any(m => m.UserId == id);

    /// <summary>Whether <paramref name="userId"/> is an effective MANAGER of <paramref name="root"/>.</summary>
    public async Task<bool> IsManagerAsync(AudienceRoot root, Guid? userId, CancellationToken cancellationToken) =>
        userId is { } id && (await EffectiveAsync(root, cancellationToken)).Any(m => m.UserId == id && m.IsManager);

    /// <summary>
    /// Whether the content of <paramref name="documentId"/> must be WITHHELD from the caller (ADR 0983): it is encrypted
    /// content — an entry of a store, or a Sealed document — and the caller is not in its effective audience. Every
    /// door that hands out stored bytes or a presigned link to them asks this, for every version: the old envelopes
    /// stay untouched, but somebody removed from the audience can no longer fetch even what was sealed to them.
    /// </summary>
    /// <remarks>A service account is never a member (it holds no key), so it is withheld too.</remarks>
    public async Task<bool> WithholdsFromCallerAsync(Guid documentId, CancellationToken cancellationToken) =>
        await RootOfAsync(documentId, cancellationToken) is { } root
        && !await IsMemberAsync(root, currentUser.UserId, cancellationToken);

    /// <summary>The declared rows, by name, as the editor shows them (a personal space has none).</summary>
    public async Task<IReadOnlyList<DeclaredAudienceMember>> DeclaredAsync(AudienceRoot root, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ContentAudienceMembers.AsNoTracking()
            .Where(m => m.DocumentId == root.DocumentId)
            .Select(m => new
            {
                m.UserId,
                m.GroupId,
                m.IsManager,
                UserName = dbContext.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                GroupName = dbContext.Groups.Where(g => g.Id == m.GroupId).Select(g => g.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return [.. rows
            .Select(r => new DeclaredAudienceMember(r.UserId, r.GroupId, r.UserName ?? r.GroupName ?? string.Empty, r.IsManager))
            .OrderBy(r => r.GroupId is null ? 0 : 1)
            .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The readers who are not yet declared: active users with a grant of read, and groups with at least one such member
    /// (a group none of whose members reads would add nobody). What an editor's picker offers, and what a PUT accepts.
    /// </summary>
    public async Task<(IReadOnlyList<(Guid Id, string DisplayName)> Users, IReadOnlyList<(Guid Id, string Name)> Groups)> CandidatesAsync(
        AudienceRoot root, CancellationToken cancellationToken)
    {
        var readers = await ReadersAsync(root, cancellationToken);
        var declared = await DeclaredAsync(root, cancellationToken);
        var declaredUsers = declared.Where(d => d.UserId is not null).Select(d => d.UserId!.Value).ToHashSet();
        var declaredGroups = declared.Where(d => d.GroupId is not null).Select(d => d.GroupId!.Value).ToHashSet();

        var readerIds = readers.Select(r => r.Id).ToHashSet();
        var groups = new List<(Guid, string)>();
        foreach (var group in await dbContext.Groups.OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken))
        {
            if (!declaredGroups.Contains(group.Id)
                && (await GroupMembershipExpansion.GetUsersEffectivelyInGroupAsync(dbContext, group.Id, cancellationToken)).Any(readerIds.Contains))
            {
                groups.Add((group.Id, group.Name));
            }
        }

        return ([.. readers.Where(r => !declaredUsers.Contains(r.Id))], groups);
    }

    /// <summary>Every active user with a grant of read on the declaring document, by name.</summary>
    public Task<IReadOnlyList<(Guid Id, string DisplayName)>> ReadersAsync(AudienceRoot root, CancellationToken cancellationToken) =>
        ReadersOfAsync(root.DocumentId, cancellationToken);

    /// <summary>Every active user with a grant of read on <paramref name="documentId"/>, by name.</summary>
    public async Task<IReadOnlyList<(Guid Id, string DisplayName)>> ReadersOfAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);
        var readers = new List<(Guid, string)>();
        foreach (var user in users)
        {
            if ((await rights.GetGrantedRightsAsync(user.Id, documentId, cancellationToken)).CanReadContent)
            {
                readers.Add((user.Id, user.DisplayName));
            }
        }

        return readers;
    }

    /// <summary>
    /// Whether the caller may move <paramref name="itemId"/> under <paramref name="targetParentId"/> as far as the
    /// audience goes (ADR 0983): a folder or entry leaving its password store for ANOTHER store must be re-sealed to the
    /// target's audience in the same act, which only somebody in both audiences can do. Everything else — a move inside
    /// one store, a store or a Sealed document carrying its own audience along — is the ACL's business alone.
    /// </summary>
    public async Task<bool> MayMoveAsync(Guid itemId, Guid targetParentId, CancellationToken cancellationToken)
    {
        if (await RootOfAsync(itemId, cancellationToken) is not { Kind: AudienceKind.PasswordStore } from
            || from.DocumentId == itemId
            || await RootOfAsync(targetParentId, cancellationToken) is not { } to
            || to.DocumentId == from.DocumentId)
        {
            return true;
        }

        return await IsMemberAsync(from, currentUser.UserId, cancellationToken)
            && await IsMemberAsync(to, currentUser.UserId, cancellationToken);
    }

    /// <summary>
    /// The first audience of a NEW store or Sealed document, added to the caller's unit of work so it commits with the
    /// document (ADR 0983). Nothing in a personal space, whose owner is the audience by computation.
    /// </summary>
    /// <remarks>
    /// <b>The creator is the first manager.</b> A service account creates without a person to name (saconsole's
    /// seeding, ADR 0966): the audience then starts as the readers it inherits from <paramref name="parentId"/>, with no
    /// manager, exactly as the migration fills an existing store — a tenant administrator appoints one.
    /// </remarks>
    public async Task SeedForNewAsync(
        Guid documentId, Guid tenantId, Guid parentId, Guid? parentPersonalOwnerId, Guid? createdByUserId, CancellationToken cancellationToken)
    {
        if (parentPersonalOwnerId is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (createdByUserId is { } creator)
        {
            dbContext.ContentAudienceMembers.Add(new ContentAudienceMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = documentId,
                UserId = creator,
                IsManager = true,
                CreatedAt = now,
            });
            return;
        }

        foreach (var (readerId, _) in await ReadersOfAsync(parentId, cancellationToken))
        {
            dbContext.ContentAudienceMembers.Add(new ContentAudienceMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = documentId,
                UserId = readerId,
                CreatedAt = now,
            });
        }

        logger.LogInformation(
            "New encrypted content {DocumentId} was created by a service account: its audience starts as the readers, with no "
            + "manager until a tenant administrator appoints one", documentId);
    }

    /// <summary>Forgets what this request computed about <paramref name="root"/>, after its audience was written.</summary>
    public void Forget(AudienceRoot root) => _effective.Remove(root.DocumentId);

    private async Task<IReadOnlyList<string>> CertificatesOfAsync(Guid userId, AudienceRoot root, CancellationToken cancellationToken)
    {
        var (certificates, outcome) = await readerCertificates.ForUserAsync(userId, cancellationToken);
        if (outcome != ReaderCertificateOutcome.Answered && certificates.Count > 0)
        {
            logger.LogDebug(
                "User {UserId} is in the audience of {DocumentId} but no module answered for their certificates ({Outcome}); "
                + "their own certificate column is not an attested enrolment, so nothing is sealed to it", userId, root.DocumentId, outcome);
            return [];
        }

        return certificates;
    }
}
