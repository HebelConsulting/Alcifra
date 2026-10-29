using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Certificates;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// The attestation evidence of one Certificate document (#1790, ADR 0963): keyed by the document and cascading with it,
// so a purged certificate takes its evidence. The source of truth the registry's AttestationRootThumbprint is derived from.
public class CertificateAttestationConfiguration : IEntityTypeConfiguration<CertificateAttestation>
{
    public void Configure(EntityTypeBuilder<CertificateAttestation> builder)
    {
        builder.HasKey(a => a.DocumentId);
        builder.Property(a => a.DocumentId).ValueGeneratedNever();
        builder.Property(a => a.Format).IsRequired().HasMaxLength(20);
        builder.Property(a => a.AttestationCertificatePem).IsRequired();
        builder.Property(a => a.IntermediateCertificatePem).IsRequired();
        builder.Property(a => a.DeviceSerial).IsRequired().HasMaxLength(64);
        builder.Property(a => a.FirmwareVersion).IsRequired().HasMaxLength(32);
        builder.Property(a => a.Slot).IsRequired().HasMaxLength(16);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(a => a.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
