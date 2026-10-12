using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// SimplArchiveMobile #20 slice B (owner-approved schema, 2026-10-12). Unique on (TenantId, Endpoint): a device
// registering again refreshes its row instead of duplicating it, which would send every notification twice.
// Cascade from the user: a deleted account takes its devices along.
public class PhonePushRegistrationConfiguration : IEntityTypeConfiguration<PhonePushRegistration>
{
    public void Configure(EntityTypeBuilder<PhonePushRegistration> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.App).IsRequired().HasMaxLength(100);
        builder.Property(r => r.DeviceLabel).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Endpoint).IsRequired().HasMaxLength(2000);
        builder.Property(r => r.P256dh).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Auth).IsRequired().HasMaxLength(200);

        builder.HasIndex(r => new { r.TenantId, r.Endpoint }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.UserId });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
