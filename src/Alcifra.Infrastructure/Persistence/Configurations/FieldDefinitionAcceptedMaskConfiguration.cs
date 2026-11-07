using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Masks;

namespace Alcifra.Infrastructure.Persistence.Configurations;

public class FieldDefinitionAcceptedMaskConfiguration : IEntityTypeConfiguration<FieldDefinitionAcceptedMask>
{
    public void Configure(EntityTypeBuilder<FieldDefinitionAcceptedMask> builder)
    {
        builder.HasKey(e => e.Id);

        // Naming a mask twice says nothing the first row did not.
        builder.HasIndex(e => new { e.TenantId, e.FieldDefinitionId, e.MaskId }).IsUnique();

        // Both sides cascade: a restriction whose field or mask is gone restricts nothing. The mask's FK is composite because
        // Mask's key is (TenantId, Id).
        builder.HasOne<FieldDefinition>()
            .WithMany()
            .HasForeignKey(e => e.FieldDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Mask>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.MaskId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
