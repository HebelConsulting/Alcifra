using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// #1970 (schema approved by the owner 2026-10-29). A device's name is unique per user; an enrolled device's credential
// key is unique installation-wide, because a login finds the row by it before the tenant is known.
public class ProtocolDeviceConfiguration : IEntityTypeConfiguration<ProtocolDevice>
{
    public void Configure(EntityTypeBuilder<ProtocolDevice> builder)
    {
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).IsRequired().HasMaxLength(100);
        builder.Property(d => d.CredentialKey).HasMaxLength(12);
        builder.Property(d => d.SecretHash).IsRequired();
        builder.Property(d => d.Kind).HasConversion<int>();

        builder.HasIndex(d => new { d.UserId, d.Name }).IsUnique();

        // Legacy rows carry no key, so the uniqueness covers enrolled devices only.
        builder.HasIndex(d => d.CredentialKey).IsUnique().HasFilter("\"CredentialKey\" IS NOT NULL");

        builder.HasIndex(d => d.TenantId);

        // Cascade: a device is part of its user, and a deleted user's devices reach nothing.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
