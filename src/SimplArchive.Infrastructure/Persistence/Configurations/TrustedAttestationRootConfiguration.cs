using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Certificates;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// The installation's trusted attestation roots (#1790, ADR 0963): installation-wide, so no tenant column and no tenant
// filter. One row per root certificate (Thumbprint unique) and per catalogue entry (CatalogueKey unique where set, so
// the healing seeder finds its own rows and uploaded roots, which carry none, never collide).
public class TrustedAttestationRootConfiguration : IEntityTypeConfiguration<TrustedAttestationRoot>
{
    public void Configure(EntityTypeBuilder<TrustedAttestationRoot> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.CertificatePem).IsRequired();
        builder.Property(r => r.Thumbprint).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(r => r.CatalogueKey).HasMaxLength(100);

        builder.HasIndex(r => r.Thumbprint).IsUnique();
        builder.HasIndex(r => r.CatalogueKey).IsUnique().HasFilter("\"CatalogueKey\" IS NOT NULL");
    }
}
