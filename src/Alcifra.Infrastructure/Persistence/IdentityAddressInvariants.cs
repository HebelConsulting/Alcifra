using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// An address names at most one person in a tenant (ADR 1015), enforced from <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// A user's own address is unique per tenant by its index, and an identity alias by its own; neither index can see the
/// other table. So an alias that equals another user's address, or an address changed to one that is somebody's alias,
/// is refused here, at the one door every write uses. Left to the endpoints, the next path that adds a user (an import,
/// a seeder) would make one address name two people, and certificate binding would accept a key for either.
/// </para>
/// <para>
/// An alias also belongs to a user of its own tenant. The API refuses each case first with a specific error; this is
/// the guarantee behind it, thrown as <see cref="InvalidOperationException"/> like the context's other invariants.
/// </para>
/// </remarks>
internal static class IdentityAddressInvariants
{
    internal static async Task ValidateAsync(AlcifraDbContext db, CancellationToken cancellationToken)
    {
        var aliases = db.ChangeTracker.Entries<UserIdentityAlias>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();
        var readdressed = db.ChangeTracker.Entries<User>()
            .Where(e => e.State is EntityState.Added
                || (e.State is EntityState.Modified && e.Property(u => u.NormalizedEmail).IsModified))
            .Select(e => e.Entity)
            .ToList();

        // IgnoreQueryFilters with the row's own tenant: a seeder writes with no ambient tenant, where the filter
        // would match nothing and every check below would pass.
        foreach (var alias in aliases)
        {
            var owner = await db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == alias.UserId)
                .Select(u => new { u.TenantId })
                .FirstOrDefaultAsync(cancellationToken);
            var ownerTenant = owner?.TenantId
                ?? readdressed.FirstOrDefault(u => u.Id == alias.UserId)?.TenantId;
            if (ownerTenant is { } tenant && tenant != alias.TenantId)
            {
                throw new InvalidOperationException(
                    $"Identity alias {alias.Id} belongs to user {alias.UserId} of another tenant.");
            }

            if (await db.Users.IgnoreQueryFilters()
                    .AnyAsync(u => u.TenantId == alias.TenantId && u.NormalizedEmail == alias.NormalizedAddress, cancellationToken)
                || readdressed.Any(u => u.TenantId == alias.TenantId && u.NormalizedEmail == alias.NormalizedAddress))
            {
                throw new InvalidOperationException(
                    $"The identity alias '{alias.Address}' is the address of a user in this tenant.");
            }
        }

        foreach (var user in readdressed)
        {
            if (await db.UserIdentityAliases.IgnoreQueryFilters()
                    .AnyAsync(a => a.TenantId == user.TenantId && a.NormalizedAddress == user.NormalizedEmail, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"The address '{user.Email}' is an identity alias of a user in this tenant.");
            }
        }
    }
}
