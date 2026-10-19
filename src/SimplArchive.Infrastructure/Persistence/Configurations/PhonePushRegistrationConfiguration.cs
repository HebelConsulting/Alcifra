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
        builder.Property(r => r.P256dh).HasMaxLength(200);
        builder.Property(r => r.Auth).HasMaxLength(200);

        // Owner-approved (2026-10-19, core #1805): Web Push or APNs. A Web Push row has its two keys and no environment; an
        // APNs row has an environment and no keys. CASE WHEN, so it stays portable across PostgreSQL and SQLite.
        builder.Property(r => r.Transport).IsRequired().HasMaxLength(20).HasDefaultValue(PushTransports.WebPush);
        builder.Property(r => r.ApnsEnvironment).HasMaxLength(20);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_PhonePushRegistrations_Transport",
            "(CASE WHEN \"Transport\" = 'webpush' AND \"P256dh\" IS NOT NULL AND \"Auth\" IS NOT NULL AND \"ApnsEnvironment\" IS NULL THEN 1 " +
            "WHEN \"Transport\" = 'apns' AND \"P256dh\" IS NULL AND \"Auth\" IS NULL AND \"ApnsEnvironment\" IN ('sandbox', 'production') THEN 1 " +
            "ELSE 0 END) = 1"));

        builder.HasIndex(r => new { r.TenantId, r.Endpoint }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.UserId });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
