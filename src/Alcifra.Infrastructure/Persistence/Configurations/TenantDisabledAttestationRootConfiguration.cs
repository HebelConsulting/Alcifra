using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Certificates;
using Alcifra.Domain.Tenants;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// A trusted attestation root a tenant switched off for itself (#1790, ADR 0963): a join row keyed by both ends, going
// with either — a deleted tenant or a removed root takes its rows.
public class TenantDisabledAttestationRootConfiguration : IEntityTypeConfiguration<TenantDisabledAttestationRoot>
{
    public void Configure(EntityTypeBuilder<TenantDisabledAttestationRoot> builder)
    {
        builder.HasKey(r => new { r.TenantId, r.RootId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TrustedAttestationRoot>()
            .WithMany()
            .HasForeignKey(r => r.RootId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
