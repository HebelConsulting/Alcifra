using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// See ADR "Configurable sensitivity labels + upload defaults". A per-tenant classification label; unique
// (TenantId, Name). The Document/MaskVersion FKs to it are configured in their own configurations.
public class SensitivityLabelDefinitionConfiguration : IEntityTypeConfiguration<SensitivityLabelDefinition>
{
    public void Configure(EntityTypeBuilder<SensitivityLabelDefinition> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(100);
        builder.Property(s => s.Color).HasMaxLength(7);

        builder.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
