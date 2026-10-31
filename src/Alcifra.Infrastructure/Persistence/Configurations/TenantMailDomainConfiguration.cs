using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Tenants;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// ADR 0628. A RECEIVING domain identifies the tenant an envelope recipient belongs to, so its unique index is
// GLOBAL rather than per-tenant: two tenants receiving for one domain is precisely the failure it prevents. An
// identity-only domain routes nothing and may sit on several tenants (ADR 1015), so the global index is filtered to
// the receiving rows, and a second index keeps one row per domain within a tenant.
public class TenantMailDomainConfiguration : IEntityTypeConfiguration<TenantMailDomain>
{
    public void Configure(EntityTypeBuilder<TenantMailDomain> builder)
    {
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Domain).IsRequired().HasMaxLength(253);

        // 253 is the DNS maximum for a fully-qualified name, so a longer one is not a domain at all.
        builder.Property(d => d.NormalizedDomain).IsRequired().HasMaxLength(253);

        builder.HasIndex(d => d.NormalizedDomain).IsUnique().HasFilter("\"IsReceiving\" = true");

        builder.HasIndex(d => new { d.TenantId, d.NormalizedDomain }).IsUnique();

        // The challenge value (#667). Nullable: a domain the stack's own configuration declared never had one,
        // because it was never challenged. Bounded because it is a generated token, not free text.
        builder.Property(d => d.VerificationToken).HasMaxLength(128);

        // Cascade: a deleted tenant's domains go with it. Nothing else may claim a domain while its tenant
        // exists, and nothing should keep claiming one after the tenant is gone.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
