using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// #1970 slice 2 (schema approved by the owner 2026-10-31). The token's hash is unique, because the anonymous link finds
// the row by it; ExpiresAt is indexed for the sweep.
public class ProtocolDeviceHandOverConfiguration : IEntityTypeConfiguration<ProtocolDeviceHandOver>
{
    public void Configure(EntityTypeBuilder<ProtocolDeviceHandOver> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(h => h.SealedProfile).IsRequired();

        builder.HasIndex(h => h.TokenHash).IsUnique();
        builder.HasIndex(h => h.ExpiresAt);
        builder.HasIndex(h => h.TenantId);

        // Cascade: revoking a device withdraws its pending hand-over.
        builder.HasOne<ProtocolDevice>()
            .WithMany()
            .HasForeignKey(h => h.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
