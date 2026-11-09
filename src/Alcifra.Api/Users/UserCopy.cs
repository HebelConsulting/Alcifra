using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Controllers;
using Alcifra.Api.Errors.Exceptions.Authorization;
using Alcifra.Api.Errors.Exceptions.Users;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Users;

/// <summary>
/// A new user as a copy of another (#2277, owner 2026-11-09): the same system rights, clearance included, and the same
/// group memberships. Grants on documents stay with the original person: a copy is a new person, not a second key to
/// someone's folders.
/// </summary>
/// <remarks>
/// Part of the create itself, so a copy is one request and one commit (ADR 0794). It used to be a create followed by a
/// PUT of the rights, and when that PUT was refused the copy existed with no rights at all. Checked by the same escalation
/// cap as a rights change, against a user who holds nothing yet; adding the memberships asks what adding a member asks,
/// the right to manage users, which the create already required.
/// </remarks>
public static class UserCopy
{
    /// <summary>The copied memberships, for the audit trail written after the commit.</summary>
    public sealed record Copied(SystemRights Rights, IReadOnlyList<Guid> GroupIds);

    /// <summary>
    /// Gives <paramref name="created"/>, not yet saved, the rights and memberships of <paramref name="sourceId"/>. Refuses,
    /// before anything is written, a source that is not a user of this tenant and rights the caller may not grant.
    /// </summary>
    public static async Task<Copied> ApplyAsync(
        AlcifraDbContext db, User created, Guid sourceId, SystemRights caller, CancellationToken cancellationToken)
    {
        // The tenant filter answers "not in this tenant" as not found, which is what it is to this caller.
        var source = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == sourceId, cancellationToken)
            ?? throw new UserToCopyNotFoundException();

        var rights = SystemRightsMapping.Read(source);
        if (!SystemRightsPolicy.CanApply(caller, new SystemRights(), rights))
        {
            throw InsufficientRightsToGrantException.OnSystemRights();
        }

        SystemRightsMapping.Apply(created, rights);
        var groups = await db.GroupMemberships.Where(m => m.UserId == sourceId).Select(m => m.GroupId).ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            db.GroupMemberships.Add(new GroupMembership { TenantId = created.TenantId, GroupId = group, UserId = created.Id });
        }

        return new Copied(rights, groups);
    }
}
