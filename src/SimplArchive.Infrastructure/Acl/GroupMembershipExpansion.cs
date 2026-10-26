using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Acl;

// A user's effective group set — the groups they're a direct member of, plus every descendant of each
// (membership flows down the tree, ADR "User/group management model"). Extracted here as a static helper
// so both EffectiveRightsCalculator (ACL entries + the IsTenantAdmin bypass) and UserSystemRightsResolver
// (system-rights union) share one expansion — see ADR "Enforce group system rights for members". No
// recursive-CTE/caching yet (deliberately deferred): loads every group in the current tenant (already
// scoped by the tenant query filter) and walks parent-to-children in memory. Fine for the modest group
// counts a real org structure has.
public static class GroupMembershipExpansion
{
    public static async Task<HashSet<Guid>> GetEffectiveGroupIdsForUserAsync(
        SimplArchiveDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var directGroupIds = await dbContext.GroupMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(cancellationToken);

        if (directGroupIds.Count == 0)
        {
            return [];
        }

        var allGroups = await dbContext.Groups
            .Select(g => new { g.Id, g.ParentGroupId })
            .ToListAsync(cancellationToken);

        var childrenByParent = allGroups
            .Where(g => g.ParentGroupId.HasValue)
            .GroupBy(g => g.ParentGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var effectiveGroupIds = new HashSet<Guid>(directGroupIds);
        var queue = new Queue<Guid>(directGroupIds);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!childrenByParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (effectiveGroupIds.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return effectiveGroupIds;
    }

    // Users who effectively belong to a group: direct members of it or any of its ANCESTORS (membership flows down, so
    // a member of a parent group belongs to the child). The inverse of GetEffectiveGroupIdsForUserAsync. One query per
    // ancestor level, then a single membership query. Moved here from AclEntriesController so the grant check and the
    // encrypted-content audience (ADR 0983) expand a group the same way.
    public static async Task<List<Guid>> GetUsersEffectivelyInGroupAsync(
        SimplArchiveDbContext dbContext, Guid groupId, CancellationToken cancellationToken)
    {
        var groupIds = new List<Guid> { groupId };
        var currentId = (Guid?)groupId;

        while (currentId is { } id)
        {
            var parentId = await dbContext.Groups.Where(g => g.Id == id).Select(g => g.ParentGroupId).SingleOrDefaultAsync(cancellationToken);
            if (parentId is { } pid && !groupIds.Contains(pid))
            {
                groupIds.Add(pid);
                currentId = pid;
            }
            else
            {
                currentId = null;
            }
        }

        return await dbContext.GroupMemberships
            .Where(m => groupIds.Contains(m.GroupId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
