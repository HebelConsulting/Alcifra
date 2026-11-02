using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// #2090 (schema approved by the owner 2026-11-02). One row per ticked shared collection of a device; the folder is
// indexed for the cascade when a folder is purged.
public class ProtocolDeviceCollectionConfiguration : IEntityTypeConfiguration<ProtocolDeviceCollection>
{
    public void Configure(EntityTypeBuilder<ProtocolDeviceCollection> builder)
    {
        builder.HasKey(c => new { c.ProtocolDeviceId, c.FolderId });

        builder.HasIndex(c => c.FolderId);
        builder.HasIndex(c => c.TenantId);

        // Cascade both ways: a revoked device's ticks go with it, and so do the ticks of a folder that is purged.
        builder.HasOne<ProtocolDevice>()
            .WithMany()
            .HasForeignKey(c => c.ProtocolDeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.FolderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
