using Microsoft.EntityFrameworkCore;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>Who an address names in the current tenant (ADR 1015).</summary>
public static class IdentityAddresses
{
    /// <summary>
    /// The user <paramref name="address"/> names: by their own address, else by a confirmed identity alias. Null when
    /// it names nobody. For what an address IDENTIFIES (a certificate's holder), never for signing in, which takes
    /// <c>User.Email</c> alone.
    /// </summary>
    public static async Task<Guid?> UserNamedByAsync(this AlcifraDbContext db, string address, CancellationToken cancellationToken)
    {
        var normalized = address.Trim().ToUpperInvariant();
        return await db.Users.Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken)
            ?? await db.UserIdentityAliases.Where(a => a.NormalizedAddress == normalized && a.ConfirmedAt != null)
                .Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(cancellationToken);
    }
}
