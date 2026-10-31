using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// ADR 1015. An address names at most one person in a tenant: unique per tenant here, and never another user's own
// address, which no index can express across two tables (IdentityAddressInvariants, in SaveChanges).
public class UserIdentityAliasConfiguration : IEntityTypeConfiguration<UserIdentityAlias>
{
    public void Configure(EntityTypeBuilder<UserIdentityAlias> builder)
    {
        builder.HasKey(a => a.Id);

        // 320 is the longest address RFC 5321 allows, as on User.Email.
        builder.Property(a => a.Address).IsRequired().HasMaxLength(320);
        builder.Property(a => a.NormalizedAddress).IsRequired().HasMaxLength(320);

        builder.HasIndex(a => new { a.TenantId, a.NormalizedAddress }).IsUnique();

        builder.HasIndex(a => a.UserId);

        // Cascade: an alias is part of its user, and a deleted user's addresses name nobody.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
